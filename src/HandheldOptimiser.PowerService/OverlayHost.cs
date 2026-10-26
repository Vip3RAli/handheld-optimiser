using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The performance overlay's level and corner, saved in HKLM, and the overlay itself: the launcher started
/// with --overlay on the player's screen, kept running while the level is not Off.
///
/// It is started with the UIAccess flag on its token, which puts its topmost window in the band Windows
/// keeps above full screen games, as Armoury Crate's overlay is (that one asks for the flag in its manifest
/// and is signed by ASUS; see SessionLauncher). Only the launcher beside this service is ever started.
/// The overlay ends itself when the level goes to Off or this service stops answering.
/// </summary>
internal sealed class OverlayHost
{
    private const string OverlayArgument = "--overlay";

    // The overlay's single instance mutex, in the player's session. The launcher's copy is OverlayApp.MutexName.
    private const string MutexName = "HandheldOptimiser.Overlay";

    private const string SettingsKey = @"SOFTWARE\HandheldOptimiser\Overlay";
    private const string LevelValue = "Level";
    private const string CornerValue = "Corner";
    private const string LayoutValue = "Layout";

    private readonly SessionProgram _program = new("Overlay", OverlayArgument, MutexName, uiAccess: true);

    public OverlayHost()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SettingsKey);
            Level = OverlayRules.SavedLevel(key?.GetValue(LevelValue) as string);
            Corner = OverlayRules.SavedCorner(key?.GetValue(CornerValue) as string);
            Layout = OverlayRules.SavedLayout(key?.GetValue(LayoutValue) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the overlay settings: {ex.Message}");
            Level = AutoTdpProtocol.OverlayOff;
            Corner = AutoTdpProtocol.TopLeft;
            Layout = AutoTdpProtocol.OverlayVertical;
        }
    }

    /// <summary>One of <see cref="AutoTdpProtocol.OverlayLevels"/>.</summary>
    public string Level { get; private set; }

    /// <summary>One of <see cref="AutoTdpProtocol.OverlayCorners"/>.</summary>
    public string Corner { get; private set; }

    /// <summary>One of <see cref="AutoTdpProtocol.OverlayLayouts"/>.</summary>
    public string Layout { get; private set; }

    /// <summary>Whether the overlay is on, which keeps the service running.</summary>
    public bool Wanted => Level != AutoTdpProtocol.OverlayOff;

    /// <summary>A new level, corner or layout from the widget. Any may be null to keep it. Saved, and started at once.</summary>
    public void Set(string? level, string? corner, string? layout)
    {
        var newLevel = OverlayRules.Level(level, Level);
        var newCorner = OverlayRules.Corner(corner, Corner);
        var newLayout = OverlayRules.Layout(layout, Layout);
        if (newLevel == Level && newCorner == Corner && newLayout == Layout)
        {
            return;
        }

        Level = newLevel;
        Corner = newCorner;
        Layout = newLayout;
        Log.Write($"Overlay: {Level}, {Corner}, {Layout}");

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SettingsKey);
            key.SetValue(LevelValue, Level, RegistryValueKind.String);
            key.SetValue(CornerValue, Corner, RegistryValueKind.String);
            key.SetValue(LayoutValue, Layout, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the overlay settings: {ex.Message}");
        }

        _program.TryNow();
        Keep();
    }

    /// <summary>Each second: starts the overlay when it is wanted and not running.</summary>
    public void Keep()
    {
        if (Wanted)
        {
            _program.Keep();
        }
    }
}
