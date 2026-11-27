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

    private const ulong SteamIdBase = 76561197960265728UL;

    /// <summary>The folder Steam is installed in, or null when Steam is not installed.</summary>
    public static string? SteamDirectory() => ReadSteamExe() is { } exe ? Path.GetDirectoryName(exe) : null;

    /// <summary>The 32-bit account id of the active, auto-login, or most recently used Steam account.</summary>
    internal static uint? ActiveAccountId32(string? steamDir = null)
    {
        if (OwnedGames.SteamAccount() is { } steamId64 && steamId64 > SteamIdBase)
        {
            return (uint)(steamId64 - SteamIdBase);
        }

        var dir = steamDir ?? SteamDirectory();
        if (dir is null)
        {
            return null;
        }

        var userData = Path.Combine(dir, "userdata");
        if (!Directory.Exists(userData))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(userData).GetDirectories()
                .Where(d => uint.TryParse(d.Name, out var id) && id > 0)
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => uint.TryParse(d.Name, out var parsed) ? (uint?)parsed : null)
                .FirstOrDefault(id => id is > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Existing Steam userdata account folders, ordered with the active account first.</summary>
    internal static IReadOnlyList<string> UserDataDirectories(string? steamDir = null)
    {
        var dir = steamDir ?? SteamDirectory();
        if (dir is null)
        {
            return [];
        }

        var userData = Path.Combine(dir, "userdata");
        if (!Directory.Exists(userData))
        {
            return [];
        }

        var ordered = new List<string>();
        try
        {
            if (ActiveAccountId32(dir) is { } activeId)
            {
                var activePath = Path.Combine(userData, activeId.ToString());
                if (Directory.Exists(activePath))
                {
                    ordered.Add(activePath);
                }
            }

            foreach (var accountDir in new DirectoryInfo(userData).GetDirectories()
                .Where(d => uint.TryParse(d.Name, out var id) && id > 0)
                .OrderByDescending(d => d.LastWriteTimeUtc))
            {
                if (!ordered.Contains(accountDir.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    ordered.Add(accountDir.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ordered;
    }

    /// <summary>Steam's own exe, or null when Steam is not installed.</summary>
    public static string? ReadSteamExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") is string exe && File.Exists(exe) ? Path.GetFullPath(exe) : null;
    }

    /// <summary>Every Steam library folder, the one Steam is installed in first.</summary>
    internal static IEnumerable<string> LibraryFolders(string steamDir)
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

        var installDir = Field(text, "installdir") is { Length: > 0 } dir
            ? Path.Combine(Path.GetDirectoryName(path)!, "common", dir)
            : null;

        var cover = FindArt(artDir, appId, CoverNames);
        return new Game(
            Key: $"steam:{appId}",
            Title: name,
            Store: GameStore.Steam,
            CoverPath: cover,
            IconPath: cover is null ? FindIcon(Path.GetDirectoryName(steamExe)!, artDir, appId) : null,
            LaunchTarget: steamExe,
            LaunchArguments: $"-silent -applaunch {appId}",
            InstallDirectory: installDir,
            HeroPath: FindArt(artDir, appId, HeroNames));
    }

    // Portrait art names, newest Steam first.
    internal static readonly string[] CoverNames = ["library_600x900.jpg", "library_capsule.jpg"];

    // The wide banner Steam shows at the top of a game's library page, or failing that its much smaller
    // store header: the widest picture Steam has of the game.
    internal static readonly string[] HeroNames = ["library_hero.jpg", "library_header.jpg", "header.jpg"];

    /// <summary>
    /// Newer Steam keeps art in a folder per app, sometimes one level deeper under a hash; older Steam
    /// used flat "{appid}_library_600x900.jpg" files.
    /// </summary>
    internal static string? FindArt(string artDir, string appId, string[] names)
    {
        try
        {
            var appDir = Path.Combine(artDir, appId);
            if (Directory.Exists(appDir))
            {
                foreach (var name in names)
                {
                    var art = Directory.EnumerateFiles(appDir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (art is not null)
                    {
                        return art;
                    }
                }
            }

            var flat = Path.Combine(artDir, $"{appId}_{names[0]}");
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
