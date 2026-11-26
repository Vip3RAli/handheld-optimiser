using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reclaims memory and processor time during gaming by terminating non-essential Windows background hosts
/// and trimming the working set of retained shell components (such as explorer.exe and TextInputHost.exe).
/// Only runs against processes in the player's interactive session, requiring no elevation.
/// </summary>
internal static partial class WindowsProcessOptimiser
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
        "StartMenuExperienceHost"
    ];

    // AMD's recording and Instant Replay (about 70 MB): opt-in because Instant Replay stops when closed.
    // RadeonSoftware itself is left untouched so Adrenalin/ADLX overlay and FreeSync stay intact.
    private static readonly string[] AmdRecordingProcesses = ["AMDRSSrcExt"];

    internal static IReadOnlyList<string> TargetProcesses(bool stopAmdRecording) =>
        stopAmdRecording ? [.. NonEssentialProcesses, .. AmdRecordingProcesses] : NonEssentialProcesses;

    /// <summary>
    /// Services any signed-in user may stop, which run all the time but are not needed during a game. Only
    /// stopped, never changed: the Claude app's Cowork service (about 24 MB) belongs to that app's package,
    /// which lets no one, administrators included, change how it starts, and the app starts it again when
    /// it needs it.
    /// </summary>
    private static readonly string[] ServicesToStop =
    [
        "CoworkVMService"
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
    public static void Optimise(bool stopAmdRecording = false)
    {
        var session = CurrentSession();
        var targets = new HashSet<string>(TargetProcesses(stopAmdRecording), StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(RetainedShellProcesses, StringComparer.OrdinalIgnoreCase);
        var terminated = 0;
        var trimmed = 0;

        Process[] allProcesses;
        try
        {
            allProcesses = Process.GetProcesses();
        }
        catch (Exception)
        {
            allProcesses = [];
        }

        foreach (var proc in allProcesses)
        {
            using (proc)
            {
                string name;
                try
                {
                    name = proc.ProcessName;
                }
                catch (Exception)
                {
                    continue;
                }

                var isTarget = targets.Contains(name);
                var isRetained = !isTarget && retained.Contains(name);
                if ((!isTarget && !isRetained) || SessionOf(proc) != session)
                {
                    continue;
                }

                try
                {
                    if (isTarget)
                    {
                        proc.Kill();
                        terminated++;
                        Program.Log($"Optimised Windows: terminated {name} (PID {proc.Id})");
                    }
                    else if (Native.EmptyWorkingSet(proc.Handle))
                    {
                        trimmed++;
                        Program.Log($"Optimised Windows: trimmed working set for {name} (PID {proc.Id})");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Already exited or permission denied
                }
            }
        }

        var stopped = ServicesToStop.Count(StopService);

        Program.Log($"Optimised Windows: terminated {terminated} process(es), stopped {stopped} service(s), trimmed {trimmed} shell host(s).");
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

    /// <summary>Asks Windows to stop a service. False when it is not installed, not running or not ours to stop.</summary>
    private static bool StopService(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, name, ServiceStop);
            if (service == 0)
            {
                return false;
            }

            try
            {
                // SERVICE_STATUS: seven numbers, filled in by Windows and not needed here.
                Span<int> status = stackalloc int[7];
                if (ControlService(service, ServiceControlStop, ref MemoryMarshal.GetReference(status)))
                {
                    Program.Log($"Optimised Windows: stopped the {name} service");
                    return true;
                }

                return false;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, ref int status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
