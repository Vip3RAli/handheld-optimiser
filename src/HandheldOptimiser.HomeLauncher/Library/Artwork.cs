using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A game's artwork from SteamGridDB, for the art its store does not keep on disk: the wide picture
/// shown behind the grid for the focused game, for players who chose artwork over the default gradient
/// of the game's colours, and the portrait cover on its tile. Steam keeps both for most of its games;
/// anything else is fetched when the player has entered an API key, and is kept on disk so each game is
/// only ever fetched once. The programs listed under Apps are looked up as well, by name, among the
/// programs SteamGridDB keeps beside its games (see <see cref="ArtworkMatch"/>).
/// </summary>
internal static class Artwork
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2/";
    private const string ArtFilter = "?types=static&mimes=image/jpeg,image/png&nsfw=false&humor=false";

    // Tiles are two wide by three tall. 600x900 is that shape and what most games have; the others are
    // the portrait sizes SteamGridDB also takes, asked for only when a game has nothing at 600x900.
    private static readonly string[] CoverSizes = ["600x900", "660x930,342x482"];

    // The row layout's tiles are square: the sizes SteamGridDB keeps its square icons at.
    private const string SquareSizes = "512x512,1024x1024";

    // An app's Old School card takes a landscape picture: the sizes SteamGridDB keeps those at. They
    // have the program's logo and name on them, where its banner artwork for a program is a plain
    // backdrop meant to go behind a logo, and says nothing on a card.
    private const string CapsuleSizes = "920x430,460x215";

    // The player's corrections for games SteamGridDB matched wrongly, one value per game key.
    private const string TitlesKey = Program.SettingsKey + @"\ArtworkTitles";

    private const int CandidatesToTry = 3;
    private const long MaxDownloadBytes = 20 * 1024 * 1024;

    // A game with no artwork today may get some later, but not so soon that every visit should ask.
    private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "artwork");

    private static readonly HttpClient Http = HttpClients.Shared;

    // Going back to a game whose artwork is still downloading joins that download.
    private static readonly InFlight<string?> Fetches = new();
    private static readonly InFlight<(string? Path, bool KeyRejected)> TileFetches = new();

    /// <summary>What a try at one kind of picture came to.</summary>
    internal enum Fetched
    {
        /// <summary>A picture was downloaded and saved.</summary>
        Saved,

        /// <summary>SteamGridDB answered, and has nothing usable: worth a marker so it is not asked again for a while.</summary>
        NotFound,

        /// <summary>It could not answer just now (a server error, or asked to slow down): no marker, ask again next time.</summary>
        TryLater
    }

    /// <summary>
    /// Whether a failed download was the server's trouble rather than the picture's: a server error, a
    /// timeout or being asked to slow down. Those are asked again next time; anything else, such as the
    /// picture being gone, counts as nothing found.
    /// </summary>
    internal static bool IsTryLater(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>
    /// Several tries at a picture as one: saved if any was, else try later if any could not be answered,
    /// else nothing found. A single server error among the candidates is enough to hold off the marker.
    /// </summary>
    internal static Fetched Combine(IEnumerable<Fetched> tries)
    {
        var result = Fetched.NotFound;
        foreach (var attempt in tries)
        {
            if (attempt == Fetched.Saved)
            {
                return Fetched.Saved;
            }

            if (attempt == Fetched.TryLater)
            {
                result = Fetched.TryLater;
            }
        }

        return result;
    }

    /// <summary>
    /// Whether to leave the marker that stops a game being asked for again for a week. Only when the
    /// service really answered with nothing; an outage would otherwise hide a game's art for a week.
    /// </summary>
    internal static bool WritesMarker(Fetched result) => result == Fetched.NotFound;

    public static bool HasApiKey => LibrarySettings.ArtworkKey is not null;

    /// <summary>The title the player said to look this game up by, if they corrected it.</summary>
    public static string? SearchTitle(Game game)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TitlesKey);
            return key?.GetValue(game.Key) is string { Length: > 0 } title ? title : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the corrected title (empty to go back to the game's own) and forgets the artwork found
    /// under the old one, so the next load searches again.
    /// </summary>
    /// <returns>False when the title could not be stored.</returns>
    public static bool SaveSearchTitle(Game game, string title)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(TitlesKey);
            if (title.Length == 0)
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, title, RegistryValueKind.String);
            }

            File.Delete(CachePath(game));
            File.Delete(PreviewPath(game));
            File.Delete(NotFoundPath(game));

            // The cover was found under the same title, so it is as wrong as the background was.
            File.Delete(CoverCachePath(game));
            File.Delete(NoCoverPath(game));
            File.Delete(SquareCachePath(game));
            File.Delete(NoSquarePath(game));
            File.Delete(CapsuleCachePath(game));
            File.Delete(NoCapsulePath(game));

            // The store's details are looked up by the same title.
            StoreDetails.Forget(game);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save the artwork title for {game.Key}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The file holding the game's artwork, downloading it first if need be, or null when it has none.
    /// Call it off the UI thread.
    /// </summary>
    public static async Task<string?> FindAsync(Game game)
    {
        // A corrected title means the player wants SteamGridDB's answer, not the store's own art.
        var corrected = SearchTitle(game);
        if (corrected is null && game.HeroPath is { } hero && File.Exists(hero))
        {
            return hero;
        }

        var cached = CachePath(game);
        if (File.Exists(cached))
        {
            return cached;
        }

        if (LibrarySettings.ArtworkKey is not { } apiKey || RecentlyNotFound(NotFoundPath(game)))
        {
            return null;
        }

        return await Fetches.RunAsync(game.Key, () => FetchAsync(game, corrected, apiKey));
    }

    /// <returns>The cached file, or null when there is no artwork or SteamGridDB could not be reached.</returns>
    private static async Task<string?> FetchAsync(Game game, string? corrected, string apiKey)
    {
        try
        {
            if (await ArtListAsync(game, "heroes", corrected, apiKey) is not { } heroes)
            {
                MarkNotFound(NotFoundPath(game));
                return null;
            }

            using var list = await GetAsync(heroes + ArtFilter, apiKey);
            var path = CachePath(game);
            var result = await SaveFirstAsync(list, path);
            if (result == Fetched.Saved)
            {
                File.Delete(PreviewPath(game));
                return path;
            }

            if (WritesMarker(result))
            {
                MarkNotFound(NotFoundPath(game));
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or IOException or UnauthorizedAccessException)
        {
            // Offline, a rejected key or a full disk: no background this time, and try again next time.
            Program.Log($"Artwork for {game.Key} could not be fetched: {ex.Message}");
            return null;
        }
    }

    /// <summary>The portrait cover already on disk for a game whose store keeps none, if it has been fetched.</summary>
    public static string? CachedCover(Game game)
    {
        var path = CoverCachePath(game);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The file holding a portrait cover for a game whose store keeps none, downloading it first if need
    /// be. Call it off the UI thread.
    /// </summary>
    /// <returns>
    /// The file, or null when there is no cover or SteamGridDB could not be reached. KeyRejected is set
    /// when SteamGridDB turned the API key away, which no amount of asking again will change.
    /// </returns>
    public static async Task<(string? Path, bool KeyRejected)> FindCoverAsync(Game game)
    {
        if (CachedCover(game) is { } cached)
        {
            return (cached, false);
        }

        // The grid asks for every tile's cover as it fills, and again as tiles come into view.
        return await TileFetches.RunAsync(game.Key + ":cover", () => FetchCoverAsync(game));
    }

    private static async Task<(string? Path, bool KeyRejected)> FetchCoverAsync(Game game)
    {
        // A game that is not installed may come with its store's own cover, which needs no key.
        if (await StoreCoverAsync(game) is { } fromStore)
        {
            return (fromStore, false);
        }

        if (LibrarySettings.ArtworkKey is not { } apiKey || RecentlyNotFound(NoCoverPath(game)))
        {
            return (null, false);
        }

        try
        {
            if (await ArtListAsync(game, "grids", SearchTitle(game), apiKey) is not { } grids)
            {
                MarkNotFound(NoCoverPath(game));
                return (null, false);
            }

            var path = CoverCachePath(game);
            var tries = new List<Fetched>();
            foreach (var size in CoverSizes)
            {
                using var list = await GetAsync($"{grids}{ArtFilter}&dimensions={size}", apiKey);
                var result = await SaveFirstAsync(list, path);
                if (result == Fetched.Saved)
                {
                    return (path, false);
                }

                tries.Add(result);
            }

            if (WritesMarker(Combine(tries)))
            {
                MarkNotFound(NoCoverPath(game));
            }

            return (null, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Program.Log($"SteamGridDB did not accept the API key ({(int)ex.StatusCode.Value}), so no covers were fetched.");
            return (null, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or IOException or UnauthorizedAccessException)
        {
            // Offline or a full disk: the tile keeps its icon, and the next start tries again.
            Program.Log($"The cover for {game.Key} could not be fetched: {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>The square icon already on disk for a game, if it has been fetched.</summary>
    public static string? CachedSquare(Game game)
    {
        var path = SquareCachePath(game);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The file holding a square icon for the game's tile in the row layout, downloading it first if need
    /// be. Only SteamGridDB has these, so there is none without a key. Call it off the UI thread.
    /// </summary>
    /// <returns>As <see cref="FindCoverAsync"/>.</returns>
    public static async Task<(string? Path, bool KeyRejected)> FindSquareAsync(Game game)
    {
        if (CachedSquare(game) is { } cached)
        {
            return (cached, false);
        }

        if (LibrarySettings.ArtworkKey is not { } apiKey || RecentlyNotFound(NoSquarePath(game)))
        {
            return (null, false);
        }

        return await TileFetches.RunAsync(game.Key + ":square", () => FetchSquareAsync(game, apiKey));
    }

    private static async Task<(string? Path, bool KeyRejected)> FetchSquareAsync(Game game, string apiKey)
    {
        try
        {
            var result = Fetched.NotFound;
            if (await ArtListAsync(game, "grids", SearchTitle(game), apiKey) is { } grids)
            {
                using var list = await GetAsync($"{grids}{ArtFilter}&dimensions={SquareSizes}", apiKey);
                var path = SquareCachePath(game);
                result = await SaveFirstAsync(list, path);
                if (result == Fetched.Saved)
                {
                    return (path, false);
                }
            }

            if (WritesMarker(result))
            {
                MarkNotFound(NoSquarePath(game));
            }

            return (null, false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Program.Log($"SteamGridDB did not accept the API key ({(int)ex.StatusCode.Value}), so no icons were fetched.");
            return (null, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or IOException or UnauthorizedAccessException)
        {
            Program.Log($"The icon for {game.Key} could not be fetched: {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>
    /// The file holding the landscape picture for an app's Old School card, downloading it first if need
    /// be. Only SteamGridDB has these, so there is none without a key. Call it off the UI thread.
    /// </summary>
    /// <returns>The file, or null when there is no such picture or SteamGridDB could not be reached.</returns>
    public static async Task<string?> FindCapsuleAsync(Game game)
    {
        var cached = CapsuleCachePath(game);
        if (File.Exists(cached))
        {
            return cached;
        }

        if (LibrarySettings.ArtworkKey is not { } apiKey || RecentlyNotFound(NoCapsulePath(game)))
        {
            return null;
        }

        // Moving along the cards asks again while the first answer is on its way, which joins it.
        return await Fetches.RunAsync(game.Key + ":capsule", () => FetchCapsuleAsync(game, apiKey));
    }

    private static async Task<string?> FetchCapsuleAsync(Game game, string apiKey)
    {
        try
        {
            var result = Fetched.NotFound;
            if (await ArtListAsync(game, "grids", SearchTitle(game), apiKey) is { } grids)
            {
                using var list = await GetAsync($"{grids}{ArtFilter}&dimensions={CapsuleSizes}", apiKey);
                var path = CapsuleCachePath(game);
                result = await SaveFirstAsync(list, path);
                if (result == Fetched.Saved)
                {
                    return path;
                }
            }

            if (WritesMarker(result))
            {
                MarkNotFound(NoCapsulePath(game));
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or IOException or UnauthorizedAccessException)
        {
            // Offline, a rejected key or a full disk: the card keeps its cover, and the next look tries again.
            Program.Log($"The card picture for {game.Key} could not be fetched: {ex.Message}");
            return null;
        }
    }

    // The stores' own image servers, the only places a cover named by a store's game list is fetched from.
    private static readonly string[] StoreImageHosts = ["steamstatic.com", "epicgames.com", "unrealengine.com", "gog.com", "gog-statics.com"];

    /// <summary>Downloads the cover a store's game list names, for a game that is not installed.</summary>
    private static async Task<string?> StoreCoverAsync(Game game)
    {
        if (game.CoverUrl is not { } link || !Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !StoreImageHosts.Any(host => url.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
            || RecentlyNotFound(NoStoreCoverPath(game)))
        {
            return null;
        }

        try
        {
            var (bytes, result) = await DownloadAsync(url);
            if (bytes is not null && IsImage(bytes))
            {
                var path = CoverCachePath(game);
                await SaveAsync(bytes, path);
                return path;
            }

            // The store's server having trouble is no reason to stop asking it for a week.
            if (WritesMarker(result))
            {
                MarkNotFound(NoStoreCoverPath(game));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"The store's cover for {game.Key} could not be fetched: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Where SteamGridDB lists one kind of art ("heroes" or "grids") for the game, or null when it does
    /// not know the game.
    /// </summary>
    private static async Task<string?> ArtListAsync(Game game, string kind, string? corrected, string apiKey)
    {
        if (corrected is null && game.Store == GameStore.Steam)
        {
            // An exact match by Steam app id, for the Steam games with no art in Steam's own cache.
            return $"{kind}/steam/{game.Key[(game.Key.IndexOf(':') + 1)..]}";
        }

        var title = Clean(corrected ?? game.Title);
        using var search = await GetAsync($"search/autocomplete/{Uri.EscapeDataString(title)}", apiKey);
        return ArtworkMatch.Pick(Results(search), title, game.IsApp, corrected is not null) is { } id ? $"{kind}/game/{id}" : null;
    }

    /// <summary>Saves the best listed picture that downloads and really is a picture.</summary>
    /// <returns>
    /// Saved; or NotFound when there were none or none of them was a picture; or TryLater when one of
    /// them could not be downloaded for the server's sake, which may well work next time.
    /// </returns>
    private static async Task<Fetched> SaveFirstAsync(JsonDocument? list, string path)
    {
        var tries = new List<Fetched>();
        foreach (var url in ImageUrls(list).Take(CandidatesToTry * 2))
        {
            var (bytes, result) = await DownloadAsync(url);
            if (bytes is null || !IsImage(bytes))
            {
                tries.Add(bytes is null ? result : Fetched.NotFound);
                continue;
            }

            await SaveAsync(bytes, path);
            return Fetched.Saved;
        }

        return Combine(tries);
    }

    private static Task SaveAsync(byte[] bytes, string path) => AtomicFile.WriteAllBytesAsync(path, bytes);

    /// <returns>The response, or null when SteamGridDB does not know the game.</returns>
    private static async Task<JsonDocument?> GetAsync(string path, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The search's answers, in SteamGridDB's order, less any with no id or name.</summary>
    private static List<(long Id, string Name)> Results(JsonDocument? search)
    {
        var results = new List<(long Id, string Name)>();
        foreach (var entry in Data(search))
        {
            if (entry.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
                && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                results.Add((value, name.GetString()!));
            }
        }

        return results;
    }

    /// <summary>
    /// Best rated first, as SteamGridDB lists them: each one at full size, then its small preview in case
    /// the full one will not download. The preview alone is too small to fill a screen cleanly.
    /// </summary>
    private static IEnumerable<Uri> ImageUrls(JsonDocument? list)
    {
        foreach (var art in Data(list))
        {
            foreach (var name in new[] { "url", "thumb" })
            {
                // Only ever download from SteamGridDB itself, whatever the response says.
                if (art.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url)
                    && url.Scheme == Uri.UriSchemeHttps
                    && (url.Host.Equals("steamgriddb.com", StringComparison.OrdinalIgnoreCase)
                        || url.Host.EndsWith(".steamgriddb.com", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return url;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Data(JsonDocument? document) =>
        document is not null && document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    /// <returns>The picture's bytes, or null with why: NotFound, or TryLater for the server's trouble.</returns>
    private static async Task<(byte[]? Bytes, Fetched Result)> DownloadAsync(Uri url)
    {
        // No API key here: the images are public, and the key is only for the API.
        // The client's timeout stops at the headers, so the body has its own deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            return (null, IsTryLater(response.StatusCode) ? Fetched.TryLater : Fetched.NotFound);
        }

        try
        {
            // Too big to be a cover is as good as none.
            var bytes = await HttpClients.ReadCappedAsync(response.Content, MaxDownloadBytes, deadline.Token);
            return (bytes, bytes is null ? Fetched.NotFound : Fetched.Saved);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // A body that stalled is the server's trouble, not a missing picture: no marker is written.
            return (null, Fetched.TryLater);
        }
    }

    /// <summary>The most a downloaded picture may be: 20 MB, far more than any cover or banner is.</summary>
    internal const long MaxPictureBytes = MaxDownloadBytes;

    /// <summary>
    /// Whether the bytes are a picture WPF can show, from its header alone. Nothing is decoded: the
    /// decoder reads the format and size and stops, where decoding it whole cost tens of MB for a large
    /// hero image just to throw it away again.
    /// </summary>
    internal static bool IsImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or IOException or ArgumentException
            or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    // Store titles carry trademark signs that SteamGridDB's names do not.
    private static string Clean(string title) =>
        string.Concat(title.Where(c => c is not ('™' or '®' or '©'))).Trim();

    private static bool RecentlyNotFound(string marker)
    {
        try
        {
            return File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < RetryNotFoundAfter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MarkNotFound(string marker)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(marker, []);
    }

    private static string CachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".hero");

    // What 0.6.0 to 0.6.2 saved: SteamGridDB's small preview, replaced by the full artwork when it is fetched.
    private static string PreviewPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".img");

    private static string NotFoundPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".none");

    private static string CoverCachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".cover");

    private static string SquareCachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".square");

    private static string NoSquarePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".nosquare");

    private static string CapsuleCachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".capsule");

    private static string NoCapsulePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".nocapsule");

    private static string NoCoverPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".nocover");

    private static string NoStoreCoverPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".nostorecover");

    // Game keys look like "steam:620", and a colon cannot be in a file name.
    private static string FileName(Game game) =>
        string.Concat(game.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
