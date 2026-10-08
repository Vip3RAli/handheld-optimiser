using System.Management;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The built-in screen's brightness, through the same WMI class Windows' brightness slider uses. Needs no
/// administrator rights. An external monitor has no such control, so on one these read and set nothing.
/// Each call takes tens of milliseconds, so keep them off the UI thread.
/// </summary>
internal static class Brightness
{
    private const string Scope = @"root\wmi";

    /// <summary>The brightness from 0 to 100, or null when the screen has no brightness control.</summary>
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (var screen in results)
            {
                using (screen)
                {
                    return Convert.ToInt32(screen["CurrentBrightness"]);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            Program.Log($"Could not read the brightness: {ex.Message}");
        }

        return null;
    }

    /// <returns>False when the screen has no brightness control or Windows refused.</returns>
    public static bool Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject screen in results)
            {
                using (screen)
                {
                    // A timeout in seconds, then the level.
                    screen.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(percent, 0, 100) });
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Program.Log($"Could not set the brightness: {ex.Message}");
        }

        return false;
    }
}
