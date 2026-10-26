using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// TDP Engine's own saved settings, in HKLM beside its crash note: Auto or Manual, the power limit and fans
/// last chosen in Manual, so Manual comes back as the player left it, and the player's own CPU boost while an
/// Auto session holds it, so a crash still gives it back. Read once as the service starts; a value that
/// cannot be read counts as missing, and Auto is the mode when none is saved.
/// </summary>
internal sealed class TdpEngineSettings
{
    private const string Key = @"SOFTWARE\HandheldOptimiser\TdpEngine";
    private const string ModeValue = "Mode";
    private const string ManualPowerLimitValue = "ManualPowerLimit";
    private const string ManualFansValue = "ManualFans";
    private const string PlayerBoostValue = "PlayerBoost";

    public TdpEngineSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            Auto = AutoMode.IsAuto(key?.GetValue(ModeValue) as string);
            ManualPowerLimit = key?.GetValue(ManualPowerLimitValue) is int watts ? watts : 0;
            ManualFans = key?.GetValue(ManualFansValue) is string { Length: > 0 } fans ? fans : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read TDP Engine's mode, so it runs in Auto: {ex.Message}");
        }
    }

    /// <summary>Whether TDP Engine picks the power limit, fans and boost itself.</summary>
    public bool Auto { get; private set; } = true;

    /// <summary>The power limit last chosen in Manual, or 0 for Auto.</summary>
    public int ManualPowerLimit { get; private set; }

    /// <summary>The fans last chosen in Manual, or null for the Ally's own.</summary>
    public string? ManualFans { get; private set; }

    /// <summary>The mode's name for a status.</summary>
    public string ModeName => Auto ? AutoTdpProtocol.AutoModeName : AutoTdpProtocol.ManualMode;

    public void SetMode(bool auto)
    {
        Auto = auto;
        Write(key => key.SetValue(ModeValue, ModeName));
    }

    /// <summary>Saves the power limit and fans chosen in Manual, when they changed.</summary>
    public void SaveManual(int powerLimit, string? fans)
    {
        if (powerLimit == ManualPowerLimit && fans == ManualFans)
        {
            return;
        }

        ManualPowerLimit = powerLimit;
        ManualFans = fans;
        Write(key =>
        {
            key.SetValue(ManualPowerLimitValue, powerLimit, RegistryValueKind.DWord);
            key.SetValue(ManualFansValue, fans ?? string.Empty);
        });
    }

    /// <summary>The player's own boost, saved while an Auto session holds it, or null when none is saved.</summary>
    public static bool? SavedPlayerBoost()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return key?.GetValue(PlayerBoostValue) is int on ? on != 0 : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Saves the player's own boost while an Auto session holds it, or with null, clears it.</summary>
    public static void SavePlayerBoost(bool? on) => Write(key =>
    {
        if (on is { } value)
        {
            key.SetValue(PlayerBoostValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(PlayerBoostValue, throwOnMissingValue: false);
        }
    });

    private static void Write(Action<RegistryKey> write)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(Key);
            write(key);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save TDP Engine's settings: {ex.Message}");
        }
    }
}
