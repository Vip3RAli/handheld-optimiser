using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// How the game library and the Game Bar widget talk to the TDP Engine service: one JSON request a
/// connection over a named pipe, answered with the status. The names and limits all three share are in
/// AutoTdpProtocol.Names.cs, which the widget builds too; this half is the service's and the library's.
/// </summary>
internal static partial class AutoTdpProtocol
{
    /// <summary>The error a start gets while the service is on its way out; the library starts it again once it has stopped.</summary>
    public const string EndingError = "TDP Engine is ending. Try again in a moment.";

    /// <summary>
    /// Where the game library saves the folders of its installed games, Apps left out, in the player's own
    /// settings. A session started from the widget only counts programs inside one of them.
    /// </summary>
    public const string LibrarySettingsKey = @"Software\HandheldOptimiser\HomeApp";
    public const string GameFoldersValue = "GameFolders";

    /// <summary>
    /// Where the service keeps the controller settings, in HKLM so they hold before anyone signs in.
    /// Replacing is 1 once the app stands in for Armoury Crate, and ControllerMode is the mode to restore.
    /// </summary>
    public const string ControlsKey = @"SOFTWARE\HandheldOptimiser\AllyControls";
    public const string ReplacingValue = "Replacing";
    public const string ControllerModeValue = "ControllerMode";

    /// <summary>The Game Bar widget's package family name, which is the only app package let through to the pipe.</summary>
    public const string WidgetPackage = "HandheldOptimiser.GameBar_pgc7cpy78bqbw";

    /// <summary>The fan curves on offer, besides the Ally's own, which is null.</summary>
    public static readonly string[] FanCurves = [QuietFans, CoolFans, FullFans];

    /// <summary>Every fan choice a request may name: the fixed curves, then Dynamic.</summary>
    public static readonly string[] FanModes = [.. FanCurves, DynamicFans];

    /// <summary>The overlay's levels, from Off to the most it shows.</summary>
    public static readonly string[] OverlayLevels = [OverlayOff, OverlaySmall, OverlayMedium, OverlayFull];

    /// <summary>The overlay's corners. The first is where it starts.</summary>
    public static readonly string[] OverlayCorners = [TopLeft, TopRight, BottomLeft, BottomRight];

    /// <summary>The overlay's layouts. The first is how it starts.</summary>
    public static readonly string[] OverlayLayouts = [OverlayVertical, OverlayHorizontal];
    public static string Write<T>(T message)
    {
        if (message is AutoTdpStatus status)
        {
            return JsonSerializer.Serialize(status, AutoTdpJsonContext.Default.AutoTdpStatus);
        }

        if (message is AutoTdpRequest request)
        {
            return JsonSerializer.Serialize(request, AutoTdpJsonContext.Default.AutoTdpRequest);
        }

        return JsonSerializer.Serialize(message);
    }

    public static T? Read<T>(string? line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            if (typeof(T) == typeof(AutoTdpStatus))
            {
                return (T?)(object?)JsonSerializer.Deserialize(line, AutoTdpJsonContext.Default.AutoTdpStatus);
            }

            if (typeof(T) == typeof(AutoTdpRequest))
            {
                return (T?)(object?)JsonSerializer.Deserialize(line, AutoTdpJsonContext.Default.AutoTdpRequest);
            }

            return JsonSerializer.Deserialize<T>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(AutoTdpRequest))]
[JsonSerializable(typeof(AutoTdpStatus))]
internal partial class AutoTdpJsonContext : JsonSerializerContext
{
}

/// <param name="Command">Start, Status, Stop or Update.</param>
/// <param name="TargetFps">The frame rate to hold, or 0 to leave the power limit alone and set only the fans.</param>
/// <param name="GameFolder">The folder the game runs from, to tell its frames from other programs'. Null counts the busiest program.</param>
/// <param name="StartWatts">Where to begin: what this game settled on last time, or 0 for halfway.</param>
/// <param name="FanCurve">One of <see cref="AutoTdpProtocol.FanModes"/>, or null for the Ally's own.</param>
/// <param name="PowerLimit">From the widget: the most watts TDP Engine may use with a target, or a limit
/// held as it is without one. 0 for Auto.</param>
/// <param name="ControllerMode">With set-controls: <see cref="AutoTdpProtocol.GamepadMode"/> or <see cref="AutoTdpProtocol.DesktopMode"/>.</param>
/// <param name="Temporary">With set-controls: hold the mode without saving it, as while Game Bar is open. With no
/// ControllerMode it ends the hold, and the saved mode is put back.</param>
/// <param name="CpuBoost">With set-boost: true to turn the processor's boost on, false to turn it off.</param>
/// <param name="OverlayLevel">With set-overlay: one of <see cref="AutoTdpProtocol.OverlayLevels"/>, or null to keep it.</param>
/// <param name="OverlayCorner">With set-overlay: one of <see cref="AutoTdpProtocol.OverlayCorners"/>, or null to keep it.</param>
/// <param name="ProcessId">With overlay: the program in front, whose frame rate is wanted.</param>
/// <param name="OverlayLayout">With set-overlay: one of <see cref="AutoTdpProtocol.OverlayLayouts"/>, or null to keep it.</param>
/// <param name="ScreenStats">With set-screen-stats: true to show the stats on the Ally's screen while docked, false to stop.</param>
/// <param name="GpuMemory">With set-gpu-memory: the size in GB, 0 for Auto, one of GpuMemory.Sizes.</param>
/// <param name="Mode">With set-mode: AutoTdpProtocol.AutoModeName or AutoTdpProtocol.ManualMode.</param>
/// <param name="RadeonFeature">With set-radeon: RadeonState.RsrName or RadeonState.AfmfName.</param>
/// <param name="RadeonOn">With set-radeon: true to switch it on, false to switch it off.</param>
internal sealed record AutoTdpRequest(string Command, int TargetFps = 0, int MinWatts = 0, int MaxWatts = 0,
    int TemperatureLimit = 0, int StartWatts = 0, string? GameFolder = null, string? FanCurve = null, int PowerLimit = 0,
    string? ControllerMode = null, bool Temporary = false, bool? CpuBoost = null, string? OverlayLevel = null,
    string? OverlayCorner = null, int ProcessId = 0, string? OverlayLayout = null, bool? ScreenStats = null,
    int? GpuMemory = null, string? Mode = null, string? RadeonFeature = null, bool? RadeonOn = null);

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
/// <param name="CpuBoost">Whether the processor's boost is on for the power source in use, or null when not known.</param>
/// <param name="OverlayLevel">The overlay's level, one of <see cref="AutoTdpProtocol.OverlayLevels"/>.</param>
/// <param name="OverlayCorner">The overlay's corner, one of <see cref="AutoTdpProtocol.OverlayCorners"/>.</param>
/// <param name="OverlayLayout">The overlay's layout, one of <see cref="AutoTdpProtocol.OverlayLayouts"/>.</param>
/// <param name="ScreenStats">Whether the stats screen on the Ally's own panel is switched on.</param>
/// <param name="GpuMemoryNow">The GPU memory Windows has now, in GB, 0 for Auto, or null when not known.</param>
/// <param name="GpuMemoryNext">The size waiting for a restart, or null when nothing waits.</param>
/// <param name="GpuMemoryOffered">The sizes that can be set, empty when this device cannot set it, or null from an older service.</param>
/// <param name="Mode">TDP Engine's mode, AutoTdpProtocol.AutoModeName or AutoTdpProtocol.ManualMode, or null from an older service.</param>
/// <param name="ManualPowerLimit">The power limit last chosen in Manual, or 0 for Auto.</param>
/// <param name="ManualFans">The fans last chosen in Manual, or null for the Ally's own.</param>
/// <param name="Rsr">Whether Radeon Super Resolution is on, or null where it is not offered or not read yet.</param>
/// <param name="Afmf">Whether AMD Fluid Motion Frames is on, or null where it is not offered or not read yet.</param>
internal sealed record AutoTdpStatus(bool Running, double? Fps = null, int? Watts = null, int? Temperature = null,
    string? State = null, string? Error = null, int TargetFps = 0, string? FanCurve = null, int PowerLimit = 0,
    bool Replacing = false, bool ControllerConnected = false, string? ControllerMode = null, bool? CpuBoost = null,
    string? OverlayLevel = null, string? OverlayCorner = null, string? OverlayLayout = null, bool ScreenStats = false,
    int? GpuMemoryNow = null, int? GpuMemoryNext = null, int[]? GpuMemoryOffered = null, string? Mode = null,
    int ManualPowerLimit = 0, string? ManualFans = null, bool? Rsr = null, bool? Afmf = null);
