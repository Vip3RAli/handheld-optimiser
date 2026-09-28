using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed games from Steam's library folders. Games are started with -silent -applaunch, which
/// starts the Steam client in the background (or hands off to it if running) without opening Big Picture.
/// </summary>
internal static partial class SteamLibrary
{
    // Tools that install through Steam but are not games.
    private static readonly HashSet<string> ExcludedAppIds = ["228980", "250820"];

    // appmanifest StateFlags bit for "fully installed"; update pending (bit 2) still counts.
    private const int StateFullyInstalled = 4;

    public static IEnumerable<Game> Scan()
    {
        var steamExe = ReadSteamExe();
        if (steamExe is null)
        {
            yield break;
        }

        var steamDir = Path.GetDirectoryName(steamExe)!;
        var artDir = Path.Combine(steamDir, "appcache", "librarycache");

        foreach (var library in LibraryFolders(steamDir))
        {
            string[] manifests;
            try
            {
                manifests = Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                var game = ReadManifest(manifest, steamExe, artDir);
                if (game is not null)
                {
                    yield return game;
                }
            }
        }
    }

    private static string? ReadSteamExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") is string exe && File.Exists(exe) ? Path.GetFullPath(exe) : null;
    }

    private static IEnumerable<string> LibraryFolders(string steamDir)
    {
        var folders = new List<string> { steamDir };

        try
        {
            var vdf = File.ReadAllText(Path.Combine(steamDir, "steamapps", "libraryfolders.vdf"));
            foreach (Match m in PathEntry().Matches(vdf))
            {
                folders.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the main library, then.
        }

        return folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Game? ReadManifest(string path, string steamExe, string artDir)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var appId = Field(text, "appid");
        var name = Field(text, "name");
        _ = int.TryParse(Field(text, "StateFlags"), out var state);

        if (appId is null || name is null || (state & StateFullyInstalled) == 0
            || ExcludedAppIds.Contains(appId) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cover = FindCover(artDir, appId);
        return new Game(
            Key: $"steam:{appId}",
            Title: name,
            Store: GameStore.Steam,
            CoverPath: cover,
            IconPath: cover is null ? FindIcon(Path.GetDirectoryName(steamExe)!, artDir, appId) : null,
            LaunchTarget: steamExe,
            LaunchArguments: $"-silent -applaunch {appId}");
    }

    // Portrait art names, newest Steam first.
    private static readonly string[] CoverNames = ["library_600x900.jpg", "library_capsule.jpg"];

    /// <summary>
    /// Newer Steam keeps art in a folder per app, sometimes one level deeper under a hash; older Steam
    /// used flat "{appid}_library_600x900.jpg" files.
    /// </summary>
    private static string? FindCover(string artDir, string appId)
    {
        try
        {
            var appDir = Path.Combine(artDir, appId);
            if (Directory.Exists(appDir))
            {
                foreach (var name in CoverNames)
                {
                    var cover = Directory.EnumerateFiles(appDir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (cover is not null)
                    {
                        return cover;
                    }
                }
            }

            var flat = Path.Combine(artDir, $"{appId}_library_600x900.jpg");
            return File.Exists(flat) ? flat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The game's desktop icon, for a tile with no cover. Steam stores it as steam\games\{hash}.ico, and
    /// the same hash names the small icon jpg at the top of the game's art folder.
    /// </summary>
    private static string? FindIcon(string steamDir, string artDir, string appId)
    {
        try
        {
            var appDir = Path.Combine(artDir, appId);
            if (!Directory.Exists(appDir))
            {
                return null;
            }

            return Directory.EnumerateFiles(appDir, "*.jpg")
                .Select(f => Path.Combine(steamDir, "steam", "games", Path.GetFileNameWithoutExtension(f) + ".ico"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Field(string vdf, string name)
    {
        var m = Regex.Match(vdf, $"\"{Regex.Escape(name)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();
}
