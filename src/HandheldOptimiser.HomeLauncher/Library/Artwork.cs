using System.Collections.Concurrent;
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
/// The wide artwork shown behind the grid for the focused game, for players who chose artwork over the
/// default gradient of the game's colours. Steam keeps one on disk for most of its
/// games; anything else comes from SteamGridDB when the player has entered an API key in the main app,
/// and is kept on disk so each game is only ever fetched once.
/// </summary>
internal static class Artwork
{
    private const string ApiBase = "https://www.steamgriddb.com/api/v2/";
    private const string ArtFilter = "?types=static&mimes=image/jpeg,image/png&nsfw=false&humor=false";

    // The player's corrections for games SteamGridDB matched wrongly, one value per game key.
    private const string TitlesKey = Program.SettingsKey + @"\ArtworkTitles";

    private const int CandidatesToTry = 3;
    private const long MaxDownloadBytes = 20 * 1024 * 1024;

    // A game with no artwork today may get some later, but not so soon that every visit should ask.
    private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "artwork");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Going back to a game whose artwork is still downloading joins that download.
    private static readonly ConcurrentDictionary<string, Task<string?>> Fetches = new(StringComparer.OrdinalIgnoreCase);

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

        if (LibrarySettings.ArtworkKey is not { } apiKey || RecentlyNotFound(game))
        {
            return null;
        }

        var fetch = Fetches.GetOrAdd(game.Key, _ => FetchAsync(game, corrected, apiKey));
        try
        {
            return await fetch;
        }
        finally
        {
            Fetches.TryRemove(game.Key, out _);
        }
    }

    /// <returns>The cached file, or null when there is no artwork or SteamGridDB could not be reached.</returns>
    private static async Task<string?> FetchAsync(Game game, string? corrected, string apiKey)
    {
        try
        {
            string heroes;
            if (corrected is null && game.Store == GameStore.Steam)
            {
                // An exact match by Steam app id, for the Steam games with no hero in Steam's own cache.
                heroes = $"heroes/steam/{game.Key[(game.Key.IndexOf(':') + 1)..]}";
            }
            else
            {
                var term = Uri.EscapeDataString(Clean(corrected ?? game.Title));
                using var search = await GetAsync($"search/autocomplete/{term}", apiKey);
                if (FirstMatchId(search) is not { } id)
                {
                    MarkNotFound(game);
                    return null;
                }

                heroes = $"heroes/game/{id}";
            }

            using var list = await GetAsync(heroes + ArtFilter, apiKey);
            foreach (var url in ImageUrls(list).Take(CandidatesToTry * 2))
            {
                var bytes = await DownloadAsync(url);
                if (bytes is null || !IsImage(bytes))
                {
                    continue;
                }

                Directory.CreateDirectory(CacheDir);
                var path = CachePath(game);
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, path, overwrite: true);
                File.Delete(PreviewPath(game));
                return path;
            }

            MarkNotFound(game);
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

    private static long? FirstMatchId(JsonDocument? search) =>
        Data(search).Select(game => game.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : (long?)null)
            .FirstOrDefault(id => id is not null);

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

    private static async Task<byte[]?> DownloadAsync(Uri url)
    {
        // No API key here: the images are public, and the key is only for the API.
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        return bytes.Length <= MaxDownloadBytes ? bytes : null;
    }

    private static bool IsImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames.Count > 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or IOException or ArgumentException or COMException)
        {
            return false;
        }
    }

    // Store titles carry trademark signs that SteamGridDB's names do not.
    private static string Clean(string title) =>
        string.Concat(title.Where(c => c is not ('™' or '®' or '©'))).Trim();

    private static bool RecentlyNotFound(Game game)
    {
        try
        {
            var marker = NotFoundPath(game);
            return File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < RetryNotFoundAfter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MarkNotFound(Game game)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(NotFoundPath(game), []);
    }

    private static string CachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".hero");

    // What 0.6.0 to 0.6.2 saved: SteamGridDB's small preview, replaced by the full artwork when it is fetched.
    private static string PreviewPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".img");

    private static string NotFoundPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".none");

    // Game keys look like "steam:620", and a colon cannot be in a file name.
    private static string FileName(Game game) =>
        string.Concat(game.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
