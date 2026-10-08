using System.Globalization;
using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What Quick Resume needs besides the game session itself: which games must not be paused, and a note
/// of the paused processes so a library that ends without resuming them (a crash, or being ended from
/// Task Manager) does not leave a game frozen for good. The next library resumes them as it starts.
/// </summary>
internal static class GamePause
{
    // Folders and files anti-cheat software puts in a game's folder, with the name to show for each. A
    // game that has one is never paused: anti-cheat watches for a game that stops responding, and the
    // best case is being thrown off the server.
    private static readonly (string Name, string Shown)[] AntiCheatFolders =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("EAAntiCheat", "EA Javelin"),
        ("GameGuard", "nProtect GameGuard"),
        ("XIGNCODE", "XIGNCODE3"),
        ("AntiCheatExpert", "Anti-Cheat Expert")
    ];

    private static readonly (string Prefix, string Shown)[] AntiCheatFiles =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("start_protected_game", "Easy Anti-Cheat"),
        ("BEService", "BattlEye"),
        ("BEClient", "BattlEye"),
        ("EAAntiCheat", "EA Javelin"),
        ("npggNT", "nProtect GameGuard"),
        ("xhunter1", "XIGNCODE3"),
        ("mhypbase", "HoYoverse's anti-cheat"),
        ("ACE-Base", "Anti-Cheat Expert")
    ];

    // Unreal games keep their anti-cheat beside the exe, three folders down (Game\Binaries\Win64).
    private const int AntiCheatDepth = 3;

    /// <summary>The anti-cheat the game's folder holds, by name, or null when none was found.</summary>
    public static string? FindAntiCheat(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = AntiCheatDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                var name = entry.Name;
                var found = entry is DirectoryInfo
                    ? AntiCheatFolders.FirstOrDefault(a => name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).Shown
                    : AntiCheatFiles.FirstOrDefault(a => name.StartsWith(a.Prefix, StringComparison.OrdinalIgnoreCase)).Shown;
                if (found is not null)
                {
                    return found;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Treated as having none: Windows still refuses to pause a protected game.
        }

        return null;
    }

    /// <summary>
    /// Whether the folder is too broad to pause everything running from it, as for a program added from
    /// a drive's root or from Windows' own folder: pausing that would freeze Windows itself.
    /// </summary>
    public static bool TooBroad(string folder) => TooBroad(folder, SystemFolders(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <param name="systemFolders">Folders whose programs must keep running, so that neither they nor a folder holding them is paused.</param>
    /// <param name="windows">Windows' own folder, nothing inside which is paused.</param>
    public static bool TooBroad(string folder, IEnumerable<string> systemFolders, string? windows)
    {
        if (Normalise(folder) is not { } full)
        {
            return true;
        }

        if (Path.GetPathRoot(full) is { } root && string.Equals(Normalise(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Normalise(windows) is { } windowsFolder && full.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return systemFolders.Select(Normalise).Any(system => system is not null && system.StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SystemFolders()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            programFiles,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(programFiles, "WindowsApps"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            AppContext.BaseDirectory
        }.Where(f => !string.IsNullOrEmpty(f));
    }

    private static string? Normalise(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            // A drive's root keeps the separator it already ends in.
            var full = Path.GetFullPath(folder);
            return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ----- The note of paused processes -----

    /// <summary>The paused processes as one setting: "1234@133700000000000000;5678@...", each id with its start time.</summary>
    public static string Format(IEnumerable<(int Id, long Started)> processes) =>
        string.Join(';', processes.Select(p => $"{p.Id.ToString(CultureInfo.InvariantCulture)}@{p.Started.ToString(CultureInfo.InvariantCulture)}"));

    public static List<(int Id, long Started)> Parse(string? text)
    {
        var processes = new List<(int Id, long Started)>();
        foreach (var part in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('@');
            if (at > 0
                && int.TryParse(part.AsSpan(0, at), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && long.TryParse(part.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var started)
                && id > 0 && started > 0)
            {
                processes.Add((id, started));
            }
        }

        return processes;
    }

    public static void Remember(IEnumerable<(int Id, long Started)> processes) => LibrarySettings.PausedProcesses = Format(processes);

    public static void Forget() => LibrarySettings.PausedProcesses = null;

    /// <summary>
    /// Resumes the processes an earlier library paused and never resumed. A process is only resumed if it
    /// started when the note says, so a later program given the same id is left alone.
    /// </summary>
    public static void ResumeLeftOver()
    {
        var left = Parse(LibrarySettings.PausedProcesses);
        if (left.Count == 0)
        {
            return;
        }

        var resumed = 0;
        foreach (var (id, started) in left)
        {
            var process = Native.OpenForPause(id);
            if (process == 0)
            {
                continue;
            }

            try
            {
                if (Native.ProcessStarted(process) == started && Native.ResumeProcess(process))
                {
                    resumed++;
                }
            }
            finally
            {
                Native.CloseProcess(process);
            }
        }

        Forget();
        Program.Log($"Resumed {resumed} of {left.Count} processes left paused by the last library");
    }
}
