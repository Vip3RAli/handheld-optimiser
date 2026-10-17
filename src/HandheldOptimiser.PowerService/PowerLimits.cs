namespace HandheldOptimiser.PowerService;

/// <summary>
/// What the Game Bar widget's power limit means. With a frame-rate target it is the most TDP Engine may
/// use; with none it is a limit held as it is, like Armoury Crate's Manual mode. 0 is Auto: TDP Engine's
/// usual range, or Armoury Crate's own limit.
/// </summary>
internal static class PowerLimits
{
    /// <summary>The range with the power limit as its top, kept between the bottom and the highest allowed.</summary>
    public static AutoTdpSettings WithTop(AutoTdpSettings settings, int powerLimit) =>
        powerLimit > 0
            ? settings with { MaxWatts = Math.Clamp(powerLimit, settings.MinWatts, AutoTdpProtocol.HighestWatts) }
            : settings;

    /// <summary>The limit to hold as it is, or null when there is a target or the limit is Auto.</summary>
    public static int? Fixed(int targetFps, int powerLimit) =>
        targetFps == 0 && powerLimit > 0
            ? Math.Clamp(powerLimit, AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts)
            : null;
}
