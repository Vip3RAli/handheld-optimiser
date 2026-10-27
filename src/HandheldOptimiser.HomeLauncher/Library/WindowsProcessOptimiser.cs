using System.ComponentModel;
using System.Diagnostics;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reclaims memory and processor time during gaming by terminating non-essential Windows background hosts
/// and trimming the working set of retained shell components (such as explorer.exe and TextInputHost.exe).
/// Only runs against processes in the player's interactive session, requiring no elevation.
/// </summary>
internal static class WindowsProcessOptimiser
{
    /// <summary>
    /// Non-essential Windows processes that provide no value during active gamepad/console gameplay and can
    /// safely be terminated. Windows restarts them automatically on demand if ever needed.
    /// </summary>
    private static readonly string[] NonEssentialProcesses =
    [
        "LockApp",
        "CrossDeviceResume",
        "CrossDeviceService",
        "SearchHost",
        "Widgets",
        "WidgetService",
        "PhoneExperienceHost",
        "SecurityHealthSystray",
        "GameBarPresenceWriter",
        "StartMenuExperienceHost",

        // The Radeon Software window, which sits open in the background (about 110 MB). Driver settings
        // such as RSR, AFMF and Anti-Lag do not need it, and Alt+R opens it again.
        "RadeonSoftware",

        // AMD's recording and Instant Replay (about 70 MB). Instant Replay stops while a game runs.
        "AMDRSSrcExt"
    ];

    /// <summary>
    /// System shell components that cannot be terminated without disrupting shell state or flashing the screen,
    /// but whose caches can be completely purged from RAM via working set trimming.
    /// </summary>
    private static readonly string[] RetainedShellProcesses =
    [
        "explorer",
        "TextInputHost",
        "TabTip"
    ];

    /// <summary>
    /// Terminates non-essential Windows hosts and trims shell working sets in the current user session.
    /// Safe to call on a background thread when a game launches.
    /// </summary>
    public static void Optimise()
    {
        var session = CurrentSession();
        var terminated = 0;
        var trimmed = 0;

        foreach (var name in NonEssentialProcesses)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var proc in procs)
            {
                using (proc)
                {
                    if (SessionOf(proc) != session)
                    {
                        continue;
                    }

                    try
                    {
                        proc.Kill();
                        terminated++;
                        Program.Log($"Optimised Windows: terminated {name} (PID {proc.Id})");
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        // Already exited or permission denied
                    }
                }
            }
        }

        foreach (var name in RetainedShellProcesses)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var proc in procs)
            {
                using (proc)
                {
                    if (SessionOf(proc) != session)
                    {
                        continue;
                    }

                    try
                    {
                        if (Native.EmptyWorkingSet(proc.Handle))
                        {
                            trimmed++;
                            Program.Log($"Optimised Windows: trimmed working set for {name} (PID {proc.Id})");
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                    }
                }
            }
        }

        Program.Log($"Optimised Windows: terminated {terminated} process(es), trimmed {trimmed} shell host(s).");
    }

    private static int CurrentSession()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static int SessionOf(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
