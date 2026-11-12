using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads games installed through Heroic Games Launcher (Epic Games via Legendary, GOG, and sideloaded games)
/// from <c>%APPDATA%\heroic</c>.
/// </summary>
internal static class HeroicLibrary
{
    private static string HeroicRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic");

    public static IEnumerable<Game> Scan()
    {
        var root = HeroicRoot;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var legendaryPath = Path.Combine(root, "legendaryConfig", "legendary", "installed.json");
        if (File.Exists(legendaryPath))
        {
            foreach (var game in ParseLegendaryInstalled(ReadAllTextSafe(legendaryPath)))
            {
                yield return game;
            }
        }

        var gogInstalledPath = Path.Combine(root, "gog_store", "installed.json");
        if (File.Exists(gogInstalledPath))
        {
            var gogCachePath = Path.Combine(root, "store_cache", "gog_library.json");
            var cacheJson = File.Exists(gogCachePath) ? ReadAllTextSafe(gogCachePath) : null;
            foreach (var game in ParseGogInstalled(ReadAllTextSafe(gogInstalledPath), cacheJson))
            {
                yield return game;
            }
        }

        var sideloadPath = Path.Combine(root, "sideload_apps", "library.json");
        if (File.Exists(sideloadPath))
        {
            foreach (var game in ParseSideloadLibrary(ReadAllTextSafe(sideloadPath)))
            {
                yield return game;
            }
        }
    }

    internal static List<Game> ParseLegendaryInstalled(string? json, bool checkDisk = true)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var games = new List<Game>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var item = prop.Value;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var appName = (item.TryGetProperty("app_name", out var a) ? a.GetString() : null) ?? prop.Name;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var installPath = item.TryGetProperty("install_path", out var p) ? p.GetString() : null;
                var exeRel = item.TryGetProperty("executable", out var e) ? e.GetString() : null;

                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(installPath))
                {
                    continue;
                }

                if (checkDisk && !Directory.Exists(installPath))
                {
                    continue;
                }

                var exePath = !string.IsNullOrWhiteSpace(exeRel)
                    ? Path.Combine(installPath, exeRel.Replace('/', Path.DirectorySeparatorChar))
                    : null;
                if (checkDisk && exePath is not null && !File.Exists(exePath))
                {
                    exePath = null;
                }

                games.Add(new Game(
                    Key: $"epic:{appName}",
                    Title: title.Trim(),
                    Store: GameStore.Epic,
                    CoverPath: null,
                    IconPath: exePath,
                    LaunchTarget: $"heroic://launch/{appName}",
                    LaunchArguments: null,
                    WorkingDirectory: installPath,
                    InstallDirectory: installPath,
                    ExecutablePath: exePath));
            }
        }
        catch (JsonException)
        {
        }

        return games;
    }

    internal static List<Game> ParseGogInstalled(string? installedJson, string? libraryCacheJson = null, bool checkDisk = true)
    {
        if (string.IsNullOrWhiteSpace(installedJson))
        {
            return [];
        }

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(libraryCacheJson))
        {
            try
            {
                using var cacheDoc = JsonDocument.Parse(libraryCacheJson);
                if (cacheDoc.RootElement.TryGetProperty("games", out var gamesArr) && gamesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in gamesArr.EnumerateArray())
                    {
                        var id = g.TryGetProperty("app_name", out var an) ? an.GetString() : null;
                        var name = g.TryGetProperty("title", out var ti) ? ti.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        {
                            titles[id] = name.Trim();
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        var games = new List<Game>();
        try
        {
            using var doc = JsonDocument.Parse(installedJson);
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Array ? inst
                : default;

            if (list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("is_dlc", out var dlc) && dlc.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var appName = (item.TryGetProperty("appName", out var a) ? a.GetString() : null)
                    ?? (item.TryGetProperty("app_name", out var a2) ? a2.GetString() : null);
                var installPath = item.TryGetProperty("install_path", out var p) ? p.GetString() : null;

                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installPath))
                {
                    continue;
                }

                if (checkDisk && !Directory.Exists(installPath))
                {
                    continue;
                }

                var title = titles.GetValueOrDefault(appName)
                    ?? (item.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } explicitTitle ? explicitTitle.Trim() : null)
                    ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(installPath));

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                games.Add(new Game(
                    Key: $"gog:{appName}",
                    Title: title,
                    Store: GameStore.Gog,
                    CoverPath: null,
                    IconPath: null,
                    LaunchTarget: $"heroic://launch/{appName}",
                    LaunchArguments: null,
                    WorkingDirectory: installPath,
                    InstallDirectory: installPath));
            }
        }
        catch (JsonException)
        {
        }

        return games;
    }

    internal static List<Game> ParseSideloadLibrary(string? json, bool checkDisk = true)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var games = new List<Game>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("games", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var item in arr.EnumerateArray())
            {
                var appName = item.TryGetProperty("app_name", out var a) ? a.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                string? exe = null;
                if (item.TryGetProperty("install", out var inst) && inst.ValueKind == JsonValueKind.Object
                    && inst.TryGetProperty("executable", out var ex))
                {
                    exe = ex.GetString();
                }

                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(exe))
                {
                    continue;
                }

                if (checkDisk && !File.Exists(exe))
                {
                    continue;
                }

                var folder = Path.GetDirectoryName(exe);
                games.Add(new Game(
                    Key: $"heroic:{appName}",
                    Title: title.Trim(),
                    Store: GameStore.Other,
                    CoverPath: null,
                    IconPath: exe,
                    LaunchTarget: exe,
                    LaunchArguments: null,
                    WorkingDirectory: folder,
                    InstallDirectory: folder,
                    ExecutablePath: exe));
            }
        }
        catch (JsonException)
        {
        }

        return games;
    }

    private static string? ReadAllTextSafe(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
