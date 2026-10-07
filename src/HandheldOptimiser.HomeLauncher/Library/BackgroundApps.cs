using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A program that runs in the background and can be closed while a game plays.</summary>
/// <param name="ExeNames">Its exe file names. The first is how it is stored.</param>
/// <param name="ReopenArguments">What to start it with afterwards, or null to leave it closed.</param>
/// <param name="ShutdownArguments">A command that asks it to close cleanly, for programs that have one.</param>
internal sealed record BackgroundApp(
    string Name,
    string[] ExeNames,
    bool OnByDefault,
    string? ReopenArguments,
    string? ShutdownArguments = null,
    bool Custom = false)
{
    public string Id => ExeNames[0].ToLowerInvariant();
}

/// <summary>A program that was closed for a game, and how to start it again.</summary>
/// <param name="Path">The exe to start it again with, or null for one Windows starts again itself.</param>
internal sealed record ClosedApp(string Name, string? Path, string Arguments);

/// <summary>
/// Closing background programs before a game, so it has the processor, memory and network to itself, and
/// starting them again after. Only programs running as this user are closed, which is all a standard user
/// may close anyway; nothing of Windows' own and no service is touched.
/// </summary>
internal static class BackgroundApps
{
    // A value per exe name: 1 to close it, 0 to leave it running. Unset means its default.
    private const string ChoicesKey = Program.SettingsKey + @"\GameMode\Apps";

    // Programs the player added, a value per exe name holding its title.
    private const string CustomKey = Program.SettingsKey + @"\GameMode\Custom";

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Programs known to run in the background and to be safe to close. Cloud sync and chat programs that
    /// people keep open on purpose (Discord, Spotify) are listed but off until switched on.
    /// </summary>
    public static readonly BackgroundApp[] Known =
    [
        new("OneDrive", ["OneDrive.exe"], true, "/background", "/shutdown"),
        new("Microsoft Teams", ["ms-teams.exe", "Teams.exe"], true, ""),
        new("Google Drive", ["GoogleDriveFS.exe"], true, ""),
        new("Dropbox", ["Dropbox.exe"], true, ""),
        // Windows starts these again by itself the next time they are needed.
        new("Phone Link", ["PhoneExperienceHost.exe"], true, null),
        new("Widgets", ["Widgets.exe", "WidgetService.exe"], true, null),
        new("Discord", ["Discord.exe"], false, "--start-minimized"),
        new("Spotify", ["Spotify.exe"], false, "")
    ];

    // Never offered from the running programs: Windows itself, the stores games need, and this library.
    private static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "HandheldOptimiser.exe", "HandheldOptimiser.HomeLauncher.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "EADesktop.exe",
        "upc.exe", "UbisoftConnect.exe", "Battle.net.exe", "GalaxyClient.exe", "XboxPcApp.exe",
        "ArmouryCrateSE.exe", "ArmouryCrate.exe", "TextInputHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "ctfmon.exe", "sihost.exe", "dwm.exe", "conhost.exe",
        "RuntimeBroker.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "GameBar.exe", "GameBarFTServer.exe"
    };

    /// <summary>Every program the player can choose to close, with whether it will be.</summary>
    public static List<(BackgroundApp App, bool Close)> Choices()
    {
        var choices = ReadChoices();
        var apps = Known.Concat(ReadCustom()).ToList();
        return apps.Select(app => (app, choices.TryGetValue(app.Id, out var close) ? close : app.OnByDefault)).ToList();
    }

    /// <returns>False when the choice could not be stored.</returns>
    public static bool SetClose(BackgroundApp app, bool close)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ChoicesKey);
            key.SetValue(app.Id, close ? 1 : 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save whether to close {app.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Adds a running program to the list, to be closed from the next game on.</summary>
    /// <returns>False when it could not be stored.</returns>
    public static bool AddCustom(string exeName, string title)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(CustomKey))
            {
                key.SetValue(exeName, title, RegistryValueKind.String);
            }

            using var choices = Registry.CurrentUser.CreateSubKey(ChoicesKey);
            choices.SetValue(exeName.ToLowerInvariant(), 1, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not add {exeName} to the programs to close: {ex.Message}");
            return false;
        }
    }

    /// <returns>False when it could not be removed.</returns>
    public static bool RemoveCustom(BackgroundApp app)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(CustomKey, writable: true))
            {
                key?.DeleteValue(app.ExeNames[0], throwOnMissingValue: false);
            }

            using var choices = Registry.CurrentUser.OpenSubKey(ChoicesKey, writable: true);
            choices?.DeleteValue(app.Id, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not remove {app.Name} from the programs to close: {ex.Message}");
            return false;
        }
    }

    /// <summary>Closes the chosen programs that are running.</summary>
    /// <param name="keep">Exe folders to leave alone, such as the game's own and its store's.</param>
    /// <returns>The programs that were closed, with what starts each one again.</returns>
    public static List<ClosedApp> CloseChosen(IEnumerable<string> keep)
    {
        var closed = new List<ClosedApp>();
        var kept = keep.ToList();
        foreach (var (app, close) in Choices())
        {
            if (close && Close(app, kept) is { } done)
            {
                closed.Add(done);
            }
        }

        Program.Log(closed.Count == 0 ? "No background programs to close" : $"Closed {string.Join(", ", closed.Select(c => c.Name))}");
        return closed;
    }

    /// <summary>Starts the programs again, in the background where they allow it.</summary>
    public static void Reopen(IEnumerable<ClosedApp> apps)
    {
        foreach (var app in apps)
        {
            if (app.Path is null)
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo(app.Path)
                {
                    Arguments = app.Arguments,
                    WorkingDirectory = Path.GetDirectoryName(app.Path) ?? string.Empty,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                })?.Dispose();
                Program.Log($"Started {app.Name} again");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                Program.Log($"Could not start {app.Name} again: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Programs running as this user that could be added to the list: one row per exe, outside Windows'
    /// own folder, and not already listed.
    /// </summary>
    public static List<(string Title, string ExeName)> Running()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        var listed = Choices().SelectMany(c => c.App.ExeNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = CurrentSession();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (SessionOf(process) != session || Native.ProcessPath(process.Id) is not { } path
                    || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var exe = Path.GetFileName(path);
                if (NeverClose.Contains(exe) || listed.Contains(exe) || found.ContainsKey(exe))
                {
                    continue;
                }

                found[exe] = AddedPrograms.TitleOf(path);
            }
        }

        return found.Select(f => (f.Value, f.Key)).OrderBy(f => f.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static ClosedApp? Close(BackgroundApp app, List<string> keep)
    {
        var session = CurrentSession();
        var processes = app.ExeNames
            .SelectMany(exe => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            .ToList();

        try
        {
            var mine = processes
                .Select(p => (Process: p, Path: Native.ProcessPath(p.Id)))
                .Where(p => SessionOf(p.Process) == session && p.Path is not null
                    && !keep.Any(folder => p.Path!.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (mine.Count == 0)
            {
                return null;
            }

            // The program's main exe, which is what starts it again: helpers often share its name.
            var path = mine.Select(p => p.Path!).FirstOrDefault(p => string.Equals(Path.GetFileName(p), app.ExeNames[0], StringComparison.OrdinalIgnoreCase))
                ?? mine[0].Path!;

            if (app.ShutdownArguments is { } shutdown)
            {
                RunAndWait(path, shutdown);
            }

            foreach (var (process, _) in mine)
            {
                if (!HasExited(process) && process.MainWindowHandle != 0)
                {
                    process.CloseMainWindow();
                }
            }

            var deadline = DateTime.UtcNow + CloseWait;
            while (DateTime.UtcNow < deadline && mine.Any(p => !HasExited(p.Process)))
            {
                Thread.Sleep(200);
            }

            var closedAny = false;
            foreach (var (process, _) in mine)
            {
                if (HasExited(process))
                {
                    closedAny = true;
                    continue;
                }

                try
                {
                    // Chat and sync programs stay in the tray when their window closes.
                    process.Kill(entireProcessTree: true);
                    closedAny = true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    Program.Log($"Could not close {app.Name}: {ex.Message}");
                }
            }

            return closedAny ? new ClosedApp(app.Name, app.ReopenArguments is null ? null : path, app.ReopenArguments ?? string.Empty) : null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void RunAndWait(string path, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true });
            process?.WaitForExit((int)CloseWait.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Closing it the ordinary way follows.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static int CurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static int SessionOf(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }

    private static Dictionary<string, bool> ReadChoices()
    {
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ChoicesKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is int value)
                {
                    choices[name] = value != 0;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The defaults, then.
        }

        return choices;
    }

    private static List<BackgroundApp> ReadCustom()
    {
        var apps = new List<BackgroundApp>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CustomKey);
            foreach (var exe in key?.GetValueNames() ?? [])
            {
                if (Known.Any(k => k.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var title = key!.GetValue(exe) as string is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(exe);
                apps.Add(new BackgroundApp(title, [exe], true, "", Custom: true));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Only the known programs, then.
        }

        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
