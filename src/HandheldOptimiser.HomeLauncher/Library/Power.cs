using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The power menu's actions. Each returns an error message, or null when Windows took the request.
/// </summary>
internal static class Power
{
    /// <summary>Whether hibernate is switched on in Windows and the device supports it.</summary>
    public static bool CanHibernate => Native.IsPwrHibernateAllowed();

    /// <param name="window">A window of ours to send the display-off request through.</param>
    public static string? Sleep(nint window)
    {
        // A Modern Standby device has no classic sleep state to ask for: it sleeps when its display goes off.
        if (Native.UsesModernStandby())
        {
            return Native.TurnDisplayOff(window) ? null : "The device could not be put to sleep.";
        }

        return Suspend(hibernate: false) is { } error ? $"The device could not be put to sleep: {error}" : null;
    }

    public static string? Hibernate() =>
        Suspend(hibernate: true) is { } error ? $"The device could not hibernate: {error}" : null;

    public static string? Restart() =>
        Shutdown("/r /t 0") is { } error ? $"The device could not be restarted: {error}" : null;

    // /hybrid keeps Fast Startup when it is on, as Shut down in the Start menu does.
    public static string? ShutDown() =>
        Shutdown("/s /hybrid /t 0") is { } error ? $"The device could not be shut down: {error}" : null;

    private static string? Suspend(bool hibernate) =>
        Native.SetSuspendState(hibernate, force: false, wakeupEventsDisabled: false)
            ? null
            : new Win32Exception(Marshal.GetLastPInvokeError()).Message;

    private static string? Shutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ex.Message;
        }
    }
}
