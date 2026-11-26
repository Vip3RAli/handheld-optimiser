namespace HandheldOptimiser.PowerService;

/// <summary>Sustained (SPL), 2-minute slow (sPPT), and 10-second fast (fPPT) package power limits in watts.</summary>
internal readonly record struct PowerEnvelope(int Sustained, int Slow, int Fast);

/// <summary>
/// What the Game Bar widget's power limit means. With a frame-rate target it is the most TDP Engine may
/// use; with none it is a limit held as it is, like Armoury Crate's Manual mode. 0 is Auto: TDP Engine's
/// usual range, or Armoury Crate's own limit.
/// </summary>
internal static class PowerLimits
{
    public const int AcSlowBoostCeiling = 43;
    public const int AcFastBoostCeiling = 53;

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

    /// <summary>
    /// Computes SPL/sPPT/fPPT limits. Flat by default, on battery, or near the temperature limit; when
    /// plugged in with <paramref name="allowAcBoost"/> and thermal headroom, grants ASUS-style short bursts
    /// up to 43 W slow / 53 W fast for faster shader compilation and level loads.
    /// </summary>
    public static PowerEnvelope Envelope(int watts, bool allowAcBoost, bool onBattery, int? temperature, int temperatureLimit)
    {
        if (!allowAcBoost || onBattery || (temperature is { } temp && temp >= temperatureLimit - 3))
        {
            return new PowerEnvelope(watts, watts, watts);
        }

        var slow = Math.Clamp(watts + 13, watts, AcSlowBoostCeiling);
        var fast = Math.Clamp(watts + 23, slow, AcFastBoostCeiling);
        return new PowerEnvelope(watts, slow, fast);
    }
}
