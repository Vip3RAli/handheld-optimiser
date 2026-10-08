using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Programs the player added to the library themselves: emulators, games from stores the library does
/// not read, or anything else with an exe or a shortcut. Each is a subkey holding its title and path,
/// and whether it is a game or an app such as a browser or Discord, which the library lists under Apps.
/// </summary>
internal static class AddedPrograms
{
    private const string ProgramsKey = Program.SettingsKey + @"\AddedPrograms";
    private const string KeyPrefix = "added:";
    private const string TitleValue = "Title";
    private const string PathValue = "Path";

    // "app" or "game". Programs added before Apps existed have none, and are sorted by LooksLikeApp.
    private const string KindValue = "Kind";
    private const string AppKind = "app";
    private const string GameKind = "game";

    /// <summary>File types the browse dialog offers, which shell execute can all start.</summary>
    public const string BrowseFilter = "Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd";

    // Start menu entries that are never the program itself.
    private static readonly string[] NotPrograms =
        ["uninstall", "readme", "read me", "help", "manual", "website", "documentation", "license", "licence", "support", "release notes", "changelog"];

    // Exes that are well known not to be games, for sorting a program into Apps when it is added.
    private static readonly HashSet<string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "librewolf.exe", "zen.exe", "arc.exe",
        // Chat and calls
        "discord.exe", "discordptb.exe", "discordcanary.exe", "telegram.exe", "signal.exe", "whatsapp.exe", "slack.exe",
        "teams.exe", "ms-teams.exe", "zoom.exe", "skype.exe", "element.exe", "vesktop.exe",
        // Music, video and streaming
        "spotify.exe", "vlc.exe", "mpc-hc64.exe", "mpv.exe", "potplayermini64.exe", "plex.exe", "plex htpc.exe", "kodi.exe",
        "obs64.exe", "streamlabs obs.exe", "moonlight.exe", "parsec.exe", "parsecd.exe", "itunes.exe", "tidal.exe", "deezer.exe",
        // Store and game launchers themselves
        "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe", "battle.net.exe", "battle.net launcher.exe", "eadesktop.exe",
        "upc.exe", "ubisoftconnect.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe",
        // Everyday tools
        "explorer.exe", "notepad++.exe", "code.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe",
        "7zfm.exe", "qbittorrent.exe", "1password.exe", "bitwarden.exe", "msiafterburner.exe", "rtss.exe"
    };

    // Programs whose shortcut starts an updater beside them, named after the folder they are in.
    private static readonly string[] KnownAppFolders = [@"\Discord\", @"\DiscordPTB\", @"\DiscordCanary\", @"\slack\", @"\Microsoft\Teams\"];

    /// <summary>The added programs whose file is still there.</summary>
    public static IEnumerable<Game> Scan()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ProgramsKey);
        foreach (var id in key?.GetSubKeyNames() ?? [])
        {
            using var entry = key!.OpenSubKey(id);
            if (entry?.GetValue(PathValue) is string path && File.Exists(path))
            {
                var title = entry.GetValue(TitleValue) as string is { Length: > 0 } name ? name : TitleOf(path);
                var kind = entry.GetValue(KindValue) as string;
                yield return ToGame(id, title, path, kind is null ? null : kind == AppKind);
            }
        }
    }

    public static bool IsAdded(Game game) => game.Key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <param name="asApp">Whether it goes under Apps, or null to go by whether it is a known app.</param>
    /// <returns>The new library entry, or null when it could not be stored.</returns>
    public static Game? Add(string path, string? title = null, bool? asApp = null)
    {
        title = string.IsNullOrWhiteSpace(title) ? TitleOf(path) : title.Trim();

        // The same file added twice keeps one entry, under the newer title.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16].ToLowerInvariant();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{ProgramsKey}\{id}");
            key.SetValue(TitleValue, title, RegistryValueKind.String);
            key.SetValue(PathValue, path, RegistryValueKind.String);
            var game = ToGame(id, title, path, asApp);
            key.SetValue(KindValue, game.IsApp ? AppKind : GameKind, RegistryValueKind.String);
            return game;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not add {path} to the library: {ex.Message}");
            return null;
        }
    }

    /// <summary>Takes a program off the library. Nothing on disk is touched.</summary>
    /// <returns>False when it could not be removed.</returns>
    public static bool Remove(Game game)
    {
        if (!IsAdded(game))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProgramsKey, writable: true);
            key?.DeleteSubKeyTree(game.Key[KeyPrefix.Length..], throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not remove {game.Key} from the library: {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves an added program between the games and Apps.</summary>
    /// <returns>The program as it is listed now, or null when the choice could not be stored.</returns>
    public static Game? SetApp(Game game, bool app)
    {
        if (!IsAdded(game))
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ProgramsKey}\{game.Key[KeyPrefix.Length..]}", writable: true);
            if (key is null)
            {
                return null;
            }

            key.SetValue(KindValue, app ? AppKind : GameKind, RegistryValueKind.String);
            return game with { IsApp = app };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not move {game.Key} to {(app ? "Apps" : "the games")}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether a program is well known not to be a game: a browser, a chat app, a media player, a store's
    /// own launcher and the like. Goes by the exe's name, or for Discord and a few others by its folder,
    /// as their shortcuts start an updater rather than the app.
    /// </summary>
    public static bool LooksLikeApp(string? exe) =>
        exe is not null
        && (KnownApps.Contains(exe[(exe.LastIndexOfAny(['\\', '/']) + 1)..])
            || KnownAppFolders.Any(folder => exe.Contains(folder, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Programs in the Start menu, for adding with the controller: every shortcut to an exe, apart from
    /// uninstallers, help files and Windows' own tools, and apart from programs inside a folder the
    /// library already lists a game from.
    /// </summary>
    /// <param name="listedFolders">Install folders of the games already in the library.</param>
    /// <returns>Each program's name, its shortcut, and the exe the shortcut starts.</returns>
    public static List<(string Title, string Shortcut, string Target)> StartMenuPrograms(IEnumerable<string> listedFolders)
    {
        var listed = listedFolders.Select(f => Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar).ToList();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        var found = new Dictionary<string, (string Title, string Shortcut, string Target)>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        })
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var title = Path.GetFileNameWithoutExtension(shortcut);
                if (NotPrograms.Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase))
                    || Shortcut.Read(shortcut) is not { Target: var target }
                    || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    || listed.Any(folder => target.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    || !File.Exists(target))
                {
                    continue;
                }

                // The same program is often in both Start menus.
                found.TryAdd(target, (title, shortcut, target));
            }
        }

        return found.Values.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The name to show for a file: a shortcut's own name, or an exe's description.</summary>
    public static string TitleOf(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() is { Length: > 0 } description)
                {
                    return description;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall back to the file name.
            }
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// A shortcut to an exe starts the exe itself, with the shortcut's arguments, so the player's own
    /// launch arguments can be added after them. Anything else is opened as it is.
    /// </summary>
    /// <param name="isApp">Whether the player put it under Apps, or null to go by <see cref="LooksLikeApp"/>.</param>
    private static Game ToGame(string id, string title, string path, bool? isApp)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        string target = path;
        string? arguments = null;
        string? workingDirectory = Path.GetDirectoryName(path);
        string? exe = extension == ".exe" ? path : null;
        string? icon = extension is ".exe" or ".lnk" ? path : null;

        if (extension == ".lnk" && Shortcut.Read(path) is { } link
            && link.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(link.Target))
        {
            target = exe = link.Target;
            arguments = link.Arguments;
            workingDirectory = link.WorkingDirectory ?? Path.GetDirectoryName(link.Target);
            icon = link.IconPath ?? link.Target;
        }
        else if (extension == ".url")
        {
            icon = UrlIcon(path);
        }

        return new Game(
            Key: KeyPrefix + id,
            Title: title,
            Store: GameStore.Other,
            CoverPath: null,
            IconPath: icon,
            LaunchTarget: target,
            LaunchArguments: arguments,
            WorkingDirectory: workingDirectory,
            InstallDirectory: exe is null ? null : Path.GetDirectoryName(exe),
            ExecutablePath: exe,
            IsApp: isApp ?? LooksLikeApp(exe));
    }

    /// <summary>The IconFile line of an internet shortcut, which Steam and Epic point at the game's exe or icon.</summary>
    private static string? UrlIcon(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["IconFile=".Length..].Trim())
                .FirstOrDefault(icon => File.Exists(icon));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads .lnk files through the shell's own shortcut object.</summary>
    private static class Shortcut
    {
        private const int MaxPath = 1024;

        public sealed record Link(string Target, string? Arguments, string? WorkingDirectory, string? IconPath);

        public static Link? Read(string path)
        {
            object? shellLink = null;
            try
            {
                shellLink = new ShellLink();
                ((IPersistFile)shellLink).Load(path, 0);
                var link = (IShellLinkW)shellLink;

                var text = new StringBuilder(MaxPath);
                link.GetPath(text, text.Capacity, 0, 0);
                var target = text.ToString();
                if (target.Length == 0)
                {
                    // An installer's "advertised" shortcut, which has no plain target to read.
                    return null;
                }

                text.Clear();
                link.GetArguments(text, text.Capacity);
                var arguments = text.Length > 0 ? text.ToString() : null;

                text.Clear();
                link.GetWorkingDirectory(text, text.Capacity);
                var workingDirectory = text.Length > 0 ? Environment.ExpandEnvironmentVariables(text.ToString()) : null;

                text.Clear();
                link.GetIconLocation(text, text.Capacity, out _);
                var icon = text.Length > 0 ? Environment.ExpandEnvironmentVariables(text.ToString()) : null;

                return new Link(Environment.ExpandEnvironmentVariables(target), arguments, workingDirectory,
                    icon is not null && File.Exists(icon) ? icon : null);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
            finally
            {
                if (shellLink is not null)
                {
                    Marshal.ReleaseComObject(shellLink);
                }
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        // Only the methods up to GetIconLocation are called; the order must match the shell's vtable.
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, nint findData, uint flags);
            void GetIDList(out nint idList);
            void SetIDList(nint idList);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int iconIndex);
        }
    }
}
