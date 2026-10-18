using System.Text.Json;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// How the game library and the Game Bar widget talk to the TDP Engine service: one JSON request a
/// connection over a named pipe, answered with the status. Built into all three, so they always agree.
/// </summary>
internal static class AutoTdpProtocol
{
    public const string ServiceName = "HandheldOptimiserAutoTdp";
    public const string PipeName = "HandheldOptimiser.AutoTdp";

    public const string Start = "start";
    public const string Status = "status";
    public const string Stop = "stop";

    /// <summary>The error a start gets while the service is on its way out; the library starts it again once it has stopped.</summary>
    public const string EndingError = "TDP Engine is ending. Try again in a moment.";

    /// <summary>From the Game Bar widget: hold this frame rate and fan curve now, starting if need be.</summary>
    public const string Update = "update";

    /// <summary>
    /// Where the game library saves the folders of its installed games, Apps left out, in the player's own
    /// settings. A session started from the widget only counts programs inside one of them.
    /// </summary>
    public const string LibrarySettingsKey = @"Software\HandheldOptimiser\HomeApp";
    public const string GameFoldersValue = "GameFolders";

    /// <summary>From the library: set the Ally controller's mode, named by the request's ControllerMode.</summary>
    public const string SetControls = "set-controls";

    /// <summary>The controller mode names a request and the saved setting carry.</summary>
    public const string GamepadMode = "gamepad";
    public const string DesktopMode = "desktop";

    /// <summary>
    /// Where the service keeps the controller settings, in HKLM so they hold before anyone signs in.
    /// Replacing is 1 once the app stands in for Armoury Crate, and ControllerMode is the mode to restore.
    /// </summary>
    public const string ControlsKey = @"SOFTWARE\HandheldOptimiser\AllyControls";
    public const string ReplacingValue = "Replacing";
    public const string ControllerModeValue = "ControllerMode";

    /// <summary>The state a status gives while a fixed power limit is held, beside the AutoTdpState names.</summary>
    public const string FixedState = "Fixed";

    /// <summary>The Game Bar widget's package family name, which is the only app package let through to the pipe.</summary>
    public const string WidgetPackage = "HandheldOptimiser.GameBar_pgc7cpy78bqbw";

    /// <summary>The fan curves on offer, besides the Ally's own, which is null.</summary>
    public const string QuietFans = "quiet";
    public const string CoolFans = "cool";
    public const string FullFans = "full";

    public static readonly string[] FanCurves = [QuietFans, CoolFans, FullFans];

    /// <summary>Moves between the fixed curves as the temperature and the power limit change. See DynamicFans.</summary>
    public const string DynamicFans = "dynamic";

    /// <summary>Every fan choice a request may name: the fixed curves, then Dynamic.</summary>
    public static readonly string[] FanModes = [.. FanCurves, DynamicFans];

    // What a request leaves at 0 gets these, as when the widget starts it without the library's settings.
    public const int DefaultMaxWatts = 25;
    public const int DefaultTemperature = 80;

    // The limits the service holds any request to, whatever the library sends. The Ally's own range in
    // Armoury Crate is 7 to 30 W.
    public const int LowestWatts = 7;
    public const int HighestWatts = 30;
    public const int LowestFps = 20;
    public const int HighestFps = 144;
    public const int LowestTemperature = 60;
    public const int HighestTemperature = 95;

    public static string Write<T>(T message) => JsonSerializer.Serialize(message);

    public static T? Read<T>(string? line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Command">Start, Status, Stop or Update.</param>
/// <param name="TargetFps">The frame rate to hold, or 0 to leave the power limit alone and set only the fans.</param>
/// <param name="GameFolder">The folder the game runs from, to tell its frames from other programs'. Null counts the busiest program.</param>
/// <param name="StartWatts">Where to begin: what this game settled on last time, or 0 for halfway.</param>
/// <param name="FanCurve">One of <see cref="AutoTdpProtocol.FanModes"/>, or null for the Ally's own.</param>
/// <param name="PowerLimit">From the widget: the most watts TDP Engine may use with a target, or a limit
/// held as it is without one. 0 for Auto.</param>
/// <param name="ControllerMode">With set-controls: <see cref="AutoTdpProtocol.GamepadMode"/> or <see cref="AutoTdpProtocol.DesktopMode"/>.</param>
/// <param name="Temporary">With set-controls: send the mode without saving it, as while Game Bar is open.</param>
internal sealed record AutoTdpRequest(string Command, int TargetFps = 0, int MinWatts = 0, int MaxWatts = 0,
    int TemperatureLimit = 0, int StartWatts = 0, string? GameFolder = null, string? FanCurve = null, int PowerLimit = 0,
    string? ControllerMode = null, bool Temporary = false);

/// <param name="Running">Whether it is setting the power limit or the fans right now.</param>
/// <param name="State">One of the AutoTdpState names.</param>
/// <param name="Watts">The power limit it set, or null when it is only setting the fans.</param>
/// <param name="Error">Why it cannot run, for the library to show, or null.</param>
/// <param name="TargetFps">The frame rate it holds, or 0 when it is only setting the fans.</param>
/// <param name="FanCurve">The fan curve it set, or null for the Ally's own.</param>
/// <param name="PowerLimit">The widget's power limit in force, or 0 for Auto.</param>
/// <param name="Replacing">Whether the app is standing in for Armoury Crate on the controller.</param>
/// <param name="ControllerConnected">Whether the Ally's controller is found and awake.</param>
/// <param name="ControllerMode">The mode the controller was last set to, or null when not known.</param>
internal sealed record AutoTdpStatus(bool Running, double? Fps = null, int? Watts = null, int? Temperature = null,
    string? State = null, string? Error = null, int TargetFps = 0, string? FanCurve = null, int PowerLimit = 0,
    bool Replacing = false, bool ControllerConnected = false, string? ControllerMode = null);

/// <summary>What TDP Engine is doing, for the library to show.</summary>
internal enum AutoTdpState
{
    /// <summary>Not enough readings yet, or waiting for the last change to take effect.</summary>
    Settling,

    /// <summary>At the target with the least power it found.</summary>
    Holding,

    /// <summary>Below the target, so the power limit goes up.</summary>
    Raising,

    /// <summary>Above the target, so the power limit comes down.</summary>
    Lowering,

    /// <summary>Below the target at the highest power limit allowed.</summary>
    AtMaximum,

    /// <summary>More power stopped raising the frame rate, as in a game held back by its processor or its own frame cap.</summary>
    NoGain,

    /// <summary>At or over the temperature limit, so the power limit comes down.</summary>
    TooHot,

    /// <summary>Hardly any frames: a loading screen, a menu, or the game paused or behind the library.</summary>
    Waiting
}
