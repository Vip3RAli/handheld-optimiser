namespace HandheldOptimiser.PowerService;

/// <summary>
/// The fan curves TDP Engine can set, in the firmware's form: eight temperatures in °C, rising, then the
/// fan speed in percent at each. The Ally's firmware runs between the points and holds the last one
/// beyond it. Every curve reaches full speed by 90 °C, so none of them can let the processor run hot.
/// </summary>
internal static class FanCurves
{
    private static readonly byte[] Temperatures = [40, 50, 60, 65, 70, 75, 80, 90];

    /// <summary>The curve for a name in <see cref="AutoTdpProtocol.FanCurves"/>, or null for the Ally's own.</summary>
    public static byte[]? For(string? name) => name switch
    {
        // Barely audible at light loads; TDP Engine's temperature limit lowers the power first if it gets warm.
        AutoTdpProtocol.QuietFans => Curve(0, 10, 20, 28, 36, 48, 62, 100),

        // Spins up early and hard, for keeping heavy games well under the limit.
        AutoTdpProtocol.CoolFans => Curve(25, 35, 50, 60, 70, 82, 95, 100),

        AutoTdpProtocol.FullFans => Curve(100, 100, 100, 100, 100, 100, 100, 100),
        _ => null
    };

    /// <summary>
    /// The curve for one of Auto's 11 steps: 0 is Quiet, 5 is Cool and 10 is Full, and a step between is each
    /// point's speed on a straight line between the two it lies between. Every one still reaches full by 90 °C.
    /// </summary>
    public static byte[] Blend(int step)
    {
        step = Math.Clamp(step, 0, FineFans.Steps);
        var half = FineFans.Steps / 2;
        var (from, to, share) = step <= half
            ? (For(AutoTdpProtocol.QuietFans)!, For(AutoTdpProtocol.CoolFans)!, (double)step / half)
            : (For(AutoTdpProtocol.CoolFans)!, For(AutoTdpProtocol.FullFans)!, (double)(step - half) / half);

        var speeds = new byte[Temperatures.Length];
        for (var i = 0; i < speeds.Length; i++)
        {
            var low = from[Temperatures.Length + i];
            var high = to[Temperatures.Length + i];
            speeds[i] = (byte)Math.Round(low + (high - low) * share, MidpointRounding.AwayFromZero);
        }

        return Curve(speeds);
    }

    private static byte[] Curve(params byte[] speeds) => [.. Temperatures, .. speeds];
}
