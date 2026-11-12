using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One achievement for a game, unlocked or locked.</summary>
internal sealed record AchievementItem(
    string Id,
    string Name,
    string? Description,
    bool Unlocked,
    DateTimeOffset? UnlockedAt = null);

/// <summary>The achievements found or synced for one game.</summary>
internal sealed record GameAchievements(
    string GameKey,
    int Unlocked,
    int Total,
    IReadOnlyList<AchievementItem> Items,
    DateTimeOffset SyncedAt)
{
    public int Percentage => Total > 0 ? (int)Math.Round(Unlocked * 100.0 / Total) : 0;

    public string Describe() => Total > 0 ? $"{Unlocked} / {Total} ({Percentage}%)" : "None";
}

/// <summary>
/// Syncs and reads achievements for Steam games (the Web API, or Steam's own stats files), GOG games (GOG
/// Galaxy's database) and games with an achievements.json in their folder. Synced achievements are kept
/// in <c>%LOCALAPPDATA%\HandheldOptimiser\achievements</c> so the library shows them at once.
/// </summary>
internal static class Achievements
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string CacheFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandheldOptimiser", "achievements");

    /// <summary>
    /// The achievements last synced for a game, or null. Only the small cached file is read: the menus and the
    /// Continue card ask on the window's thread, and store files are read when a sync runs, off it.
    /// </summary>
    public static GameAchievements? For(Game game) =>
        ReadCache(game.Key) is { Total: > 0 } cached ? cached : null;

    /// <summary>Syncs achievements for one game from its store or local database and updates the cache.</summary>
    public static async Task<GameAchievements?> SyncGameAsync(Game game)
    {
        if (game.Store == GameStore.Steam && SteamAppId(game) is { } appId)
        {
            var apiKey = LibrarySettings.SteamKey;
            var steamId = StoreAccounts.Read(GameStore.Steam).UserId ?? FirstSteamId64();
            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(steamId))
            {
                var remote = await FetchSteamAchievementsAsync(game.Key, appId, steamId, apiKey);
                if (remote is { Total: > 0 })
                {
                    WriteCache(remote);
                    return remote;
                }
            }
        }

        var local = await Task.Run(() => ReadLocal(game));
        if (local is { Total: > 0 })
        {
            WriteCache(local);
            return local;
        }

        return ReadCache(game.Key);
    }

    /// <summary>Syncs achievements for all installed games in the library.</summary>
    /// <returns>How many games had achievements, and the total unlocked across them.</returns>
    public static async Task<(int Games, int Unlocked)> SyncAllAsync(IEnumerable<Game> games)
    {
        var count = 0;
        var unlocked = 0;

        foreach (var game in games.Where(g => g.Installed && !g.IsApp))
        {
            var synced = await SyncGameAsync(game);
            if (synced is { Total: > 0 })
            {
                count++;
                unlocked += synced.Unlocked;
            }
        }

        return (count, unlocked);
    }

    // ----- Steam Web API -----

    private static async Task<GameAchievements?> FetchSteamAchievementsAsync(string gameKey, string appId, string steamId, string apiKey)
    {
        var url = $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v0001/?appid={Uri.EscapeDataString(appId)}&key={Uri.EscapeDataString(apiKey)}&steamid={Uri.EscapeDataString(steamId)}&l=english";
        try
        {
            var json = await Http.GetStringAsync(url);
            return ParseSteamPlayerAchievements(gameKey, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Program.Log($"Could not fetch Steam achievements for {gameKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses the JSON returned by Steam's <c>GetPlayerAchievements/v0001</c> endpoint.</summary>
    internal static GameAchievements? ParseSteamPlayerAchievements(string gameKey, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("playerstats", out var stats)
                || !stats.TryGetProperty("achievements", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = new List<AchievementItem>();
            foreach (var entry in list.EnumerateArray())
            {
                var id = entry.TryGetProperty("apiname", out var api) ? api.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var name = entry.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : id;
                var desc = entry.TryGetProperty("description", out var d) && d.GetString() is { Length: > 0 } text ? text : null;
                var achieved = entry.TryGetProperty("achieved", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() != 0;
                DateTimeOffset? unlockedAt = null;
                if (achieved && entry.TryGetProperty("unlocktime", out var u) && u.ValueKind == JsonValueKind.Number && u.GetInt64() is > 0 and var unix)
                {
                    unlockedAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                }

                items.Add(new AchievementItem(id, name, desc, achieved, unlockedAt));
            }

            if (items.Count == 0)
            {
                return null;
            }

            var unlockedCount = items.Count(i => i.Unlocked);
            return new GameAchievements(gameKey, unlockedCount, items.Count, items, DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- Local Store Databases & Caches -----

    private static GameAchievements? ReadLocal(Game game)
    {
        if (ReadGogGalaxyAchievements(GogLibrary.DefaultDatabasePath, game) is { Total: > 0 } gog)
        {
            return gog;
        }

        if (game.Store == GameStore.Steam && SteamAppId(game) is { } appId && SteamLibrary.SteamDirectory() is { } steamPath)
        {
            var account = StoreAccounts.Read(GameStore.Steam).UserId is { } steamId && ulong.TryParse(steamId, out var id64)
                && id64 > SteamIdBase
                ? (id64 - SteamIdBase).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            if (ReadSteamStats(Path.Combine(steamPath, "appcache", "stats"), game.Key, appId, account) is { Total: > 0 } localSteam)
            {
                return localSteam;
            }
        }

        if (game.InstallDirectory is { } folder && Directory.Exists(folder))
        {
            if (ReadFolderAchievementsJson(game.Key, folder) is { Total: > 0 } folderAch)
            {
                return folderAch;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a GOG game's achievements from GOG Galaxy 2.0's database. <c>UserAchievements</c> has a row for
    /// every achievement with whether it is unlocked, and <c>LocalizedAchievements</c> its name, in English
    /// (language 16). Release keys are <c>gog_&lt;product id&gt;</c>. Checked against a real database on
    /// 2026-11-11. Galaxy also lists games from linked stores, under keys this library does not share, so only
    /// GOG's own games are read.
    /// </summary>
    internal static GameAchievements? ReadGogGalaxyAchievements(string dbPath, Game game)
    {
        if (game.Store != GameStore.Gog || !File.Exists(dbPath))
        {
            return null;
        }

        var releaseKey = $"gog_{RawId(game)}".Replace("'", "''");
        var rows = Sqlite.Query(
            dbPath,
            "SELECT u.apikey, l.name, l.description, u.isUnlocked FROM UserAchievements u "
            + "LEFT JOIN LocalizedAchievements l ON l.gameReleaseKey = u.gameReleaseKey AND l.apikey = u.apikey AND l.languageId = 16 "
            + $"WHERE u.gameReleaseKey = '{releaseKey}';",
            4);

        var items = (rows ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r[0]))
            .Select(r => new AchievementItem(r[0]!, string.IsNullOrWhiteSpace(r[1]) ? r[0]! : r[1]!,
                string.IsNullOrWhiteSpace(r[2]) ? null : r[2], r[3] == "1"))
            .ToList();

        return items.Count > 0
            ? new GameAchievements(game.Key, items.Count(i => i.Unlocked), items.Count, items, DateTimeOffset.UtcNow)
            : null;
    }

    /// <summary>
    /// Reads a Steam game's achievements from Steam's own stats files, for when there is no Web API key or no
    /// connection. Both are binary KeyValues, which <see cref="SteamAppInfo.ReadObject"/> reads:
    /// <c>UserGameStatsSchema_&lt;app&gt;.bin</c> has <c>&lt;app&gt;/stats/&lt;stat&gt;</c>, and a stat of
    /// achievements has <c>bits/&lt;bit&gt;</c>, each with its <c>name</c> and <c>display/name|desc/english</c>.
    /// <c>UserGameStats_&lt;account&gt;_&lt;app&gt;.bin</c> has <c>cache/&lt;stat&gt;/data</c>, a number whose
    /// bits are the achievements unlocked. Checked against this device's files on 2026-11-11.
    /// </summary>
    /// <param name="account">The Steam account's 32-bit id, whose file is read when there are several.</param>
    internal static GameAchievements? ReadSteamStats(string statsDir, string gameKey, string appId, string? account)
    {
        var schemaPath = Path.Combine(statsDir, $"UserGameStatsSchema_{appId}.bin");
        try
        {
            if (!File.Exists(schemaPath))
            {
                return null;
            }

            var userFiles = Directory.GetFiles(statsDir, $"UserGameStats_*_{appId}.bin");
            var userFile = userFiles.FirstOrDefault(f => account is not null
                    && Path.GetFileName(f).Equals($"UserGameStats_{account}_{appId}.bin", StringComparison.OrdinalIgnoreCase))
                ?? userFiles.FirstOrDefault();

            return ParseSteamStats(gameKey, appId, File.ReadAllBytes(schemaPath),
                userFile is null ? null : File.ReadAllBytes(userFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Turns a stats schema and the player's stats (null when none) into the game's achievements.</summary>
    internal static GameAchievements? ParseSteamStats(string gameKey, string appId, byte[] schema, byte[]? user)
    {
        try
        {
            var position = 0;
            var schemaRoot = SteamAppInfo.ReadObject(schema, ref position, schema.Length, names: null, depth: 0);
            if ((schemaRoot.GetValueOrDefault(appId) ?? schemaRoot.Values.FirstOrDefault()) is not Dictionary<string, object> app
                || app.GetValueOrDefault("stats") is not Dictionary<string, object> stats)
            {
                return null;
            }

            Dictionary<string, object>? cache = null;
            if (user is not null)
            {
                position = 0;
                cache = SteamAppInfo.ReadObject(user, ref position, user.Length, names: null, depth: 0)
                    .GetValueOrDefault("cache") as Dictionary<string, object>;
            }

            var items = new List<AchievementItem>();
            foreach (var (statId, value) in stats)
            {
                if (value is not Dictionary<string, object> stat || stat.GetValueOrDefault("bits") is not Dictionary<string, object> bits)
                {
                    continue;
                }

                var unlockedBits = cache?.GetValueOrDefault(statId) is Dictionary<string, object> saved && saved.GetValueOrDefault("data") is int data
                    ? unchecked((uint)data)
                    : 0u;

                foreach (var (bitText, bitValue) in bits.OrderBy(b => int.TryParse(b.Key, out var n) ? n : int.MaxValue))
                {
                    if (bitValue is not Dictionary<string, object> bit || bit.GetValueOrDefault("name") is not string { Length: > 0 } name
                        || !int.TryParse(bitText, out var index) || index is < 0 or > 31)
                    {
                        continue;
                    }

                    var display = bit.GetValueOrDefault("display") as Dictionary<string, object>;
                    var title = English(display?.GetValueOrDefault("name")) ?? name;
                    var description = English(display?.GetValueOrDefault("desc"));
                    items.Add(new AchievementItem(name, title, description, (unlockedBits & (1u << index)) != 0));
                }
            }

            return items.Count > 0
                ? new GameAchievements(gameKey, items.Count(i => i.Unlocked), items.Count, items, DateTimeOffset.UtcNow)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A schema text: a plain string, or one per language, of which the English is taken.</summary>
    private static string? English(object? text) => text switch
    {
        string { Length: > 0 } plain => plain,
        Dictionary<string, object> languages when languages.GetValueOrDefault("english") is string { Length: > 0 } english => english,
        _ => null
    };

    internal static GameAchievements? ReadFolderAchievementsJson(string gameKey, string folder)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(folder, "achievements.json"),
            Path.Combine(folder, "steam_settings", "achievements.json")
        })
        {
            try
            {
                if (File.Exists(candidate) && ParseGenericAchievementsJson(gameKey, File.ReadAllText(candidate)) is { Total: > 0 } parsed)
                {
                    return parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    internal static GameAchievements? ParseGenericAchievementsJson(string gameKey, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = new List<AchievementItem>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var id = (elem.TryGetProperty("name", out var n) ? n.GetString() : null)
                    ?? (elem.TryGetProperty("id", out var i) ? i.GetString() : null);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var display = (elem.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null)
                    ?? (elem.TryGetProperty("title", out var t) ? t.GetString() : null)
                    ?? id;
                var desc = elem.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var earned = (elem.TryGetProperty("earned", out var e) && e.ValueKind == JsonValueKind.True)
                    || (elem.TryGetProperty("unlocked", out var u) && u.ValueKind == JsonValueKind.True)
                    || (elem.TryGetProperty("achieved", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() != 0);

                items.Add(new AchievementItem(id, display, desc, earned));
            }

            return items.Count > 0
                ? new GameAchievements(gameKey, items.Count(x => x.Unlocked), items.Count, items, DateTimeOffset.UtcNow)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- Cache on disk -----

    internal static GameAchievements? ReadCache(string gameKey, string? cacheFolder = null)
    {
        var path = CachePath(gameKey, cacheFolder ?? CacheFolder);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GameAchievements>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteCache(GameAchievements achievements, string? cacheFolder = null)
    {
        var folder = cacheFolder ?? CacheFolder;
        try
        {
            Directory.CreateDirectory(folder);
            var path = CachePath(achievements.GameKey, folder);
            File.WriteAllText(path, JsonSerializer.Serialize(achievements, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not cache achievements for {achievements.GameKey}: {ex.Message}");
        }
    }

    private static string CachePath(string gameKey, string folder)
    {
        var safe = string.Concat(gameKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(folder, safe + ".json");
    }

    // Steam's 64-bit ids are this plus the account's 32-bit id.
    private const ulong SteamIdBase = 76561197960265728UL;

    // A non-Steam game added to Steam has no achievements, and its made-up app id is not one Steam knows.
    private static string? SteamAppId(Game game) =>
        game.Key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) && !SteamShortcuts.IsShortcut(game)
            ? game.Key["steam:".Length..]
            : null;

    private static string RawId(Game game)
    {
        var colon = game.Key.IndexOf(':');
        return colon >= 0 ? game.Key[(colon + 1)..] : game.Key;
    }

    private static string? FirstSteamId64()
    {
        if (SteamLibrary.SteamDirectory() is not { } path)
        {
            return null;
        }

        var userdata = Path.Combine(path, "userdata");
        try
        {
            if (Directory.Exists(userdata))
            {
                foreach (var dir in Directory.GetDirectories(userdata))
                {
                    if (uint.TryParse(Path.GetFileName(dir), out var id32) && id32 > 0)
                    {
                        return (76561197960265728L + id32).ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
