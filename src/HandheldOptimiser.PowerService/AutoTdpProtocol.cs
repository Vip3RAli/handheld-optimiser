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

    /// <summary>From the Game Bar widget: hold this frame rate and fan curve now, starting if need be.</summary>
    public const string Update = "update";

    /// <summary>The Game Bar widget's package family name, which is the only app package let through to the pipe.</summary>
    public const string WidgetPackage = "HandheldOptimiser.GameBar_pgc7cpy78bqbw";

    /// <summary>The fan curves on offer, besides the Ally's own, which is null.</summary>
    public const string QuietFans = "quiet";
    public const string CoolFans = "cool";
    public const string FullFans = "full";

    public static readonly string[] FanCurves = [QuietFans, CoolFans, FullFans];

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
/// <param name="FanCurve">One of <see cref="AutoTdpProtocol.FanCurves"/>, or null for the Ally's own.</param>
internal sealed record AutoTdpRequest(string Command, int TargetFps = 0, int MinWatts = 0, int MaxWatts = 0,
    int TemperatureLimit = 0, int StartWatts = 0, string? GameFolder = null, string? FanCurve = null);

/// <param name="Running">Whether it is setting the power limit or the fans right now.</param>
/// <param name="State">One of the AutoTdpState names.</param>
/// <param name="Watts">The power limit it set, or null when it is only setting the fans.</param>
/// <param name="Error">Why it cannot run, for the library to show, or null.</param>
/// <param name="TargetFps">The frame rate it holds, or 0 when it is only setting the fans.</param>
/// <param name="FanCurve">The fan curve it set, or null for the Ally's own.</param>
internal sealed record AutoTdpStatus(bool Running, double? Fps = null, int? Watts = null, int? Temperature = null,
    string? State = null, string? Error = null, int TargetFps = 0, string? FanCurve = null);

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
