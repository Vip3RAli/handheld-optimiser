namespace HandheldOptimiser.PowerService;

/// <summary>
/// TDP Engine's Auto mode, in which it picks the power limit, the fans and the processor's boost itself for
/// every session, with or without a frame rate to hold. What Auto sets a session up with is here, with no
/// Windows calls, so it can be tested. Manual is everything as before.
/// </summary>
internal static class AutoMode
{
    /// <summary>The Ally's highest power limit plugged in and on battery, Armoury Crate's Turbo, as the widget's slider tops out.</summary>
    public const int PluggedInTop = 30;
    public const int BatteryTop = 25;

    /// <summary>The top of Auto's power range for the power source in use.</summary>
    public static int TopWatts(bool onBattery) => onBattery ? BatteryTop : PluggedInTop;

    /// <summary>Whether a saved or requested mode is Auto. Only Manual is Manual, so a missing one is Auto.</summary>
    public static bool IsAuto(string? mode) => mode != AutoTdpProtocol.ManualMode;

    /// <summary>
    /// What a session in Auto runs with: the frame rate asked for, or with none, one no game reaches, so the
    /// controller looks for the most frames for the least power; and the whole power range for the power
    /// source, whatever range or limit the request named.
    /// </summary>
    /// <param name="targetFps">The frame rate asked for, or 0 for none.</param>
    /// <param name="temperatureLimit">The temperature limit asked for, or 0 for TDP Engine's own.</param>
    public static AutoTdpSettings Settings(int targetFps, int temperatureLimit, bool onBattery) => new(
        targetFps > 0 ? Math.Clamp(targetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps) : AutoTdpProtocol.NoTargetFps,
        AutoTdpProtocol.LowestWatts,
        TopWatts(onBattery),
        temperatureLimit > 0
            ? Math.Clamp(temperatureLimit, AutoTdpProtocol.LowestTemperature, AutoTdpProtocol.HighestTemperature)
            : AutoTdpProtocol.DefaultTemperature);

    /// <summary>The frame rate a status shows: 0 for no target, as the widget and library read it.</summary>
    public static int ShownTarget(int targetFps) => targetFps == AutoTdpProtocol.NoTargetFps ? 0 : targetFps;
}
