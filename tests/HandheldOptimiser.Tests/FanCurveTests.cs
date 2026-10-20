using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public class FanCurveTests
{
    public static TheoryData<string> Curves => [.. AutoTdpProtocol.FanCurves];

    [Theory]
    [MemberData(nameof(Curves))]
    public void Each_curve_is_in_the_firmwares_form_and_reaches_full_speed_by_90(string name)
    {
        var curve = FanCurves.For(name);

        Assert.NotNull(curve);
        Assert.Equal(16, curve.Length);

        var temperatures = curve[..8];
        var speeds = curve[8..];
        Assert.Equal(temperatures.Order(), temperatures);
        Assert.Equal(temperatures.Length, temperatures.Distinct().Count());
        Assert.Equal(speeds.Order(), speeds);
        Assert.All(speeds, speed => Assert.InRange(speed, (byte)0, (byte)100));
        Assert.True(temperatures[^1] <= 90);
        Assert.Equal(100, speeds[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData("loud")]
    public void Anything_else_leaves_the_Allys_own_curve(string? name) => Assert.Null(FanCurves.For(name));
}
