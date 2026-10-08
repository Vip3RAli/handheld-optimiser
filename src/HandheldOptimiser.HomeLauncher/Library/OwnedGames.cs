using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Games the player owns but has not installed, for the library's Not installed tab. Pressing A on one
/// opens its store's install page.
///
/// Epic and GOG Galaxy keep the list on the device, so it is read from there and nothing is sent anywhere.
/// Steam only lists owned games through its Web API, which needs the player's own key; the answer is kept
/// on disk and asked for again at most every few hours.
/// </summary>
internal static class OwnedGames
{
    private static readonly TimeSpan SteamRefreshAfter = TimeSpan.FromHours(12);

    // Steam ids are an account number added to this.
    private const ulong SteamIdBase = 76561197960265728;

    // Tools that install through Steam but are not games, as in SteamLibrary.
    private static readonly HashSet<string> ExcludedSteamApps = ["228980", "250820"];

    // The Epic Games Launcher's copy of the catalogue entries for the account's games: base64 JSON.
    private static readonly string EpicCatalog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EpicGamesLauncher", "Saved", "Data", "Catalog", "catcache.bin");

    private static readonly string GalaxyDatabase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "owned");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Each source's games, kept until its file changes: the library scans every couple of minutes.
    private static readonly Dictionary<string, (DateTime Written, List<Game> Games)> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The GOG Galaxy query. Release keys name a game and its store, as in gog_1207658924; Galaxy also
    // lists games from other stores it is linked to, which their own sources cover.
    private const string GalaxyQuery = """
        SELECT p.releaseKey, t.type, p.value
        FROM GamePieces p
        JOIN GamePieceTypes t ON t.id = p.gamePieceTypeId
        WHERE p.releaseKey LIKE 'gog\_%' ESCAPE '\'
          AND t.type IN ('title', 'originalTitle', 'originalImages')
          AND p.releaseKey IN (SELECT releaseKey FROM LibraryReleases)
        """;

    /// <summary>The owned games that are not among the installed ones, by title. Call it off the UI thread.</summary>
    public static List<Game> NotInstalled(IReadOnlySet<string> installedKeys)
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        foreach (var (store, read) in new (string, Func<List<Game>>)[]
        {
            ("Steam", SteamGames),
            ("Epic", () => Cached(EpicCatalog, path => ParseEpicCatalog(File.ReadAllText(path)))),
            ("GOG", () => Cached(GalaxyDatabase, GogGames))
        })
        {
            try
            {
                foreach (var game in read().Where(g => !installedKeys.Contains(g.Key)))
                {
                    games.TryAdd(game.Key, game);
                }
            }
            catch (Exception ex)
            {
                // One store's odd file must not take the others' games with it.
                Program.Log($"Listing the owned {store} games failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return games.Values.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>How many Steam games the last answer from Steam listed, or null when none has been read yet.</summary>
    public static int? SteamCount()
    {
        if (SteamAccount() is not { } id)
        {
            return null;
        }

        lock (Cache)
        {
            return Cache.TryGetValue(SteamCachePath(id), out var cached) ? cached.Games.Count : null;
        }
    }

    /// <summary>
    /// Asks Steam for the account's games when the player has a key and the last answer is old, or always
    /// when <paramref name="force"/> is set.
    /// </summary>
    /// <returns>Whether the list changed, and a message for the player when Steam could not be asked.</returns>
    public static async Task<(bool Changed, string? Error)> RefreshSteamAsync(bool force)
    {
        if (LibrarySettings.SteamKey is not { } key)
        {
            return (false, null);
        }

        if (SteamAccount() is not { } steamId)
        {
            return (false, "Sign in to Steam once, so the library knows which account's games to list.");
        }

        var path = SteamCachePath(steamId);
        try
        {
            if (!force && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < SteamRefreshAfter)
            {
                return (false, null);
            }

            var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/"
                + $"?key={Uri.EscapeDataString(key)}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json";
            using var response = await Http.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, "Steam did not accept the Web API key.");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            if (ParseSteamOwned(json, null).Count == 0)
            {
                // A private profile answers with no games at all. The last good list is kept.
                return (false, "Steam listed no games. In Steam's privacy settings, set Game details to Public.");
            }

            var before = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            if (json == before)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return (false, null);
            }

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not ask Steam for the owned games: {ex.Message}");
            return (false, "Steam could not be reached. The last list is kept.");
        }
    }

    private static List<Game> SteamGames()
    {
        if (SteamAccount() is not { } steamId)
        {
            return [];
        }

        var artDir = SteamLibrary.SteamDirectory() is { } steamDir ? Path.Combine(steamDir, "appcache", "librarycache") : null;
        return Cached(SteamCachePath(steamId), path => ParseSteamOwned(File.ReadAllText(path), artDir));
    }

    /// <summary>The Steam games in a GetOwnedGames answer, as games to install.</summary>
    /// <param name="artDir">Steam's art folder, where it keeps covers for games it has shown in its library.</param>
    public static List<Game> ParseSteamOwned(string json, string? artDir)
    {
        var games = new List<Game>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("games", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return games;
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (!entry.TryGetProperty("appid", out var id) || !id.TryGetInt64(out var appId)
                    || Str(entry, "name") is not { } name || ExcludedSteamApps.Contains(appId.ToString()))
                {
                    continue;
                }

                var cover = artDir is null ? null : SteamLibrary.FindArt(artDir, appId.ToString(), SteamLibrary.CoverNames);
                games.Add(new Game(
                    Key: $"steam:{appId}",
                    Title: name,
                    Store: GameStore.Steam,
                    CoverPath: cover,
                    IconPath: null,
                    LaunchTarget: $"steam://install/{appId}",
                    Installed: false,
                    CoverUrl: cover is null ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg" : null));
            }
        }
        catch (JsonException)
        {
            // A broken answer lists nothing; the next one may be better.
        }

        return games;
    }

    /// <summary>
    /// The games in the Epic Games Launcher's catalogue cache. Add-ons and DLC, which name the game they
    /// belong to, and anything that does not run on Windows are left out.
    /// </summary>
    public static List<Game> ParseEpicCatalog(string base64)
    {
        var games = new List<Game>();
        string json;
        try
        {
            json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch (FormatException)
        {
            return games;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return games;
            }

            foreach (var item in doc.RootElement.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var isGame = item.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                    && categories.EnumerateArray().Any(c => Str(c, "path") == "games");
                var isAddOn = item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object;

                if (!isGame || isAddOn || Str(item, "id") is not { } itemId || Str(item, "namespace") is not { } ns
                    || Str(item, "title") is not { } title || WindowsAppId(item) is not { } appId)
                {
                    continue;
                }

                games.Add(new Game(
                    Key: $"epic:{appId}",
                    Title: title,
                    Store: GameStore.Epic,
                    CoverPath: null,
                    IconPath: null,
                    LaunchTarget: $"com.epicgames.launcher://apps/{Uri.EscapeDataString($"{ns}:{itemId}:{appId}")}?action=install",
                    Installed: false,
                    CoverUrl: KeyImage(item, "DieselGameBoxTall") ?? KeyImage(item, "OfferImageTall")));
            }
        }
        catch (JsonException)
        {
            // A cache the launcher was halfway through writing; the next scan reads it again.
        }

        return games;
    }

    private static List<Game> GogGames(string path)
    {
        if (Sqlite.Query(path, GalaxyQuery, 3) is not { } rows)
        {
            Program.Log("GOG Galaxy's game list could not be read");
            return [];
        }

        return ParseGalaxyRows(rows);
    }

    /// <summary>GOG games from the rows of <see cref="GalaxyQuery"/>: release key, piece type, and its JSON.</summary>
    public static List<Game> ParseGalaxyRows(IEnumerable<string?[]> rows)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var originalTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var covers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row is not [{ } release, { } type, { } value])
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(value);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (type)
                {
                    case "title" when Str(root, "title") is { } title:
                        titles[release] = title;
                        break;
                    case "originalTitle" when Str(root, "title") is { } title:
                        originalTitles[release] = title;
                        break;
                    case "originalImages" when Str(root, "verticalCover") is { } cover:
                        // Galaxy keeps WebP; GOG's image server gives the same picture as a JPEG.
                        covers[release] = cover.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? cover[..^5] + ".jpg" : cover;
                        break;
                }
            }
            catch (JsonException)
            {
            }
        }

        return titles.Keys.Union(originalTitles.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(release => release.StartsWith("gog_", StringComparison.OrdinalIgnoreCase))
            .Select(release => new Game(
                Key: $"gog:{release[4..]}",
                Title: titles.GetValueOrDefault(release) ?? originalTitles[release],
                Store: GameStore.Gog,
                CoverPath: null,
                IconPath: null,
                LaunchTarget: $"goggalaxy://openGameView/{release[4..]}",
                Installed: false,
                CoverUrl: covers.GetValueOrDefault(release)))
            .ToList();
    }

    /// <summary>The Steam id of the account signed in now, or else of the one that signed in last.</summary>
    private static ulong? SteamAccount()
    {
        try
        {
            using (var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                if (active?.GetValue("ActiveUser") is int user && user != 0)
                {
                    return SteamIdBase + (uint)user;
                }
            }

            if (SteamLibrary.SteamDirectory() is not { } steamDir)
            {
                return null;
            }

            var users = KeyValues.Parse(File.ReadAllText(Path.Combine(steamDir, "config", "loginusers.vdf")))?.Block("users");
            foreach (var (id, user) in users?.Blocks ?? [])
            {
                if (user.Value("MostRecent") == "1" && ulong.TryParse(id, out var steamId))
                {
                    return steamId;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return null;
    }

    private static string SteamCachePath(ulong steamId) => Path.Combine(CacheDir, $"steam-{steamId}.json");

    /// <summary>A source's games, read again only once its file has changed. Missing files list nothing.</summary>
    private static List<Game> Cached(string path, Func<string, List<Game>> read)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var written = File.GetLastWriteTimeUtc(path);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Written == written)
            {
                return cached.Games;
            }
        }

        var games = read(path);
        lock (Cache)
        {
            Cache[path] = (written, games);
        }

        return games;
    }

    private static string? WindowsAppId(JsonElement item)
    {
        if (!item.TryGetProperty("releaseInfo", out var releases) || releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var release in releases.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
        {
            // Some entries list no platforms at all; those are taken to run anywhere.
            var windows = !release.TryGetProperty("platform", out var platforms) || platforms.ValueKind != JsonValueKind.Array
                || platforms.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "Windows");
            if (windows && Str(release, "appId") is { } appId)
            {
                return appId;
            }
        }

        return null;
    }

    private static string? KeyImage(JsonElement item, string type) =>
        item.TryGetProperty("keyImages", out var images) && images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object && Str(i, "type") == type)
                .Select(i => Str(i, "url")).FirstOrDefault(u => u is not null)
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
