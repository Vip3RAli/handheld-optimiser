using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The stats screen: big readings and the time on the Ally's own panel while it sits under a monitor. The
/// switch is saved in HKLM, and while it is on the launcher is kept running with --screen-stats in the
/// player's session. That program does the rest, as the player: it turns the panel on beside the monitor,
/// shows the stats there, and puts the screens back when it is switched off.
/// </summary>
internal sealed class ScreenStatsHost
{
    private const string ScreenStatsArgument = "--screen-stats";

    // The stats screen's single instance mutex, in the player's session. The launcher's copy is ScreenStatsApp.MutexName.
    private const string MutexName = "HandheldOptimiser.ScreenStats";

    private const string SettingsKey = @"SOFTWARE\HandheldOptimiser\ScreenStats";
    private const string OnValue = "On";

    // Not given UIAccess: it is on a screen of its own, so there is nothing for it to be kept above.
    private readonly SessionProgram _program = new("Stats screen", ScreenStatsArgument, MutexName, uiAccess: false);

    public ScreenStatsHost()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKey);
            On = key?.GetValue(OnValue) is 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the stats screen setting: {ex.Message}");
        }
    }

    /// <summary>Whether it is switched on, which keeps the service running.</summary>
    public bool On { get; private set; }

    /// <summary>Switched on or off from the widget. Saved, and started at once.</summary>
    public void Set(bool on)
    {
        if (on == On)
        {
            return;
        }

        On = on;
        Log.Write($"Stats screen: {(on ? "on" : "off")}");

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SettingsKey);
            key.SetValue(OnValue, on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the stats screen setting: {ex.Message}");
        }

        _program.TryNow();
        Keep();
    }

    /// <summary>Each second: starts the stats screen when it is on and not running.</summary>
    public void Keep()
    {
        if (On)
        {
            _program.Keep();
        }
    }
}
