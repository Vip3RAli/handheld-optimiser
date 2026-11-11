using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The controller settings the service keeps in HKLM: whether the app stands in for Armoury Crate, and the
/// mode to put the controller in each time it is found. HKLM, so they hold before anyone signs in.
/// </summary>
internal static class AllyControlsStore
{
    private const string GamingConfigurationKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string OemWidgetValue = "OEMGameBarWidget";

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

    /// <summary>
    /// Names the TDP Engine widget as Game Bar's OEM widget in HKLM, so Game Bar puts it first on the
    /// Compact Mode bar (before the Home button) as it did for ASUS Command Center.
    /// </summary>
    public static void SetOemWidget()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(GamingConfigurationKey);
            if (!string.Equals(key.GetValue(OemWidgetValue) as string, AutoTdpProtocol.WidgetPackage, StringComparison.Ordinal))
            {
                key.SetValue(OemWidgetValue, AutoTdpProtocol.WidgetPackage, RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not set the Game Bar OEM widget: {ex.Message}");
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

    /// <summary>What the back paddles (M1 and M2) are set to do, or <see cref="AutoTdpProtocol.PaddleNone"/>.</summary>
    public static string SavedPaddleAction()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutoTdpProtocol.ControlsKey);
            return AllyCommands.ParsePaddleAction(key?.GetValue(AutoTdpProtocol.PaddleActionValue) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the saved paddle action: {ex.Message}");
            return AutoTdpProtocol.PaddleNone;
        }
    }

    /// <summary>Saves what the back paddles (M1 and M2) do.</summary>
    public static void SavePaddleAction(string action)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AutoTdpProtocol.ControlsKey);
            key.SetValue(AutoTdpProtocol.PaddleActionValue, AllyCommands.ParsePaddleAction(action), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the paddle action: {ex.Message}");
        }
    }

    /// <summary>
    /// The saved thumbstick light ring mode, or null when the player has not picked one. The lights are left as
    /// they are until then, so whatever Armoury Crate or the player set before stays.
    /// </summary>
    public static string? SavedLighting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutoTdpProtocol.ControlsKey);
            var saved = key?.GetValue(AutoTdpProtocol.ControllerLightingValue) as string;
            return AllyCommands.IsLightingMode(saved) ? AllyCommands.ParseLighting(saved) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the saved controller lighting: {ex.Message}");
            return null;
        }
    }

    /// <summary>The saved thumbstick light ring colour, or Xbox green when none is saved.</summary>
    public static string SavedColour()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AutoTdpProtocol.ControlsKey);
            return AllyCommands.ParseColour(key?.GetValue(AutoTdpProtocol.LightingColourValue) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the saved stick colour: {ex.Message}");
            return AutoTdpProtocol.ColourGreen;
        }
    }

    /// <summary>Saves the thumbstick light ring colour.</summary>
    public static void SaveColour(string colour)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AutoTdpProtocol.ControlsKey);
            key.SetValue(AutoTdpProtocol.LightingColourValue, AllyCommands.ParseColour(colour), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the stick colour: {ex.Message}");
        }
    }

    /// <summary>Saves the thumbstick light ring mode to set each time the controller is found.</summary>
    public static void SaveLighting(string lighting)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AutoTdpProtocol.ControlsKey);
            key.SetValue(AutoTdpProtocol.ControllerLightingValue, AllyCommands.ParseLighting(lighting), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the controller lighting: {ex.Message}");
        }
    }
}
