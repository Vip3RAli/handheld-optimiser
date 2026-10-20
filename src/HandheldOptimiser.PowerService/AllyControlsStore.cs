using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The controller settings the service keeps in HKLM: whether the app stands in for Armoury Crate, and the
/// mode to put the controller in each time it is found. HKLM, so they hold before anyone signs in.
/// </summary>
internal static class AllyControlsStore
{
    /// <summary>Whether the app is standing in for Armoury Crate. Not set, or unreadable, counts as no.</summary>
    public static bool Replacing()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutoTdpProtocol.ControlsKey);
            return key?.GetValue(AutoTdpProtocol.ReplacingValue) is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read whether the app replaces Armoury Crate: {ex.Message}");
            return false;
        }
    }

    /// <summary>The mode last saved, or null when none is saved or it cannot be read.</summary>
    public static ControllerMode? SavedMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutoTdpProtocol.ControlsKey);
            return key?.GetValue(AutoTdpProtocol.ControllerModeValue) is string name ? AllyCommands.ParseMode(name) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the saved controller mode: {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves the mode to set each time the controller is found.</summary>
    public static void SaveMode(ControllerMode mode)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AutoTdpProtocol.ControlsKey);
            key.SetValue(AutoTdpProtocol.ControllerModeValue, AllyCommands.ModeName(mode), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the controller mode: {ex.Message}");
        }
    }
}
