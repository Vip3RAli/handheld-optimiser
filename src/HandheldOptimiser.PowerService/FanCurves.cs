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

    private static byte[] Curve(params byte[] speeds) => [.. Temperatures, .. speeds];
}
