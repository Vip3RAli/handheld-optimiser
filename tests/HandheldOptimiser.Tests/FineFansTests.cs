using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The fans in Auto: 11 steps blended from Quiet through Cool to Full, and when TDP Engine moves between them.
/// </summary>
public class FineFansTests
{
    private const int Limit = 80;
    private const int Top = 30;
    private static readonly DateTime Start = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    // ----- The curves -----

    [Fact]
    public void Steps_0_5_and_10_are_Quiet_Cool_and_Full()
    {
        Assert.Equal(FanCurves.For(AutoTdpProtocol.QuietFans), FanCurves.Blend(0));
        Assert.Equal(FanCurves.For(AutoTdpProtocol.CoolFans), FanCurves.Blend(5));
        Assert.Equal(FanCurves.For(AutoTdpProtocol.FullFans), FanCurves.Blend(10));
    }

    [Fact]
    public void Step_3_lies_between_Quiet_and_Cool()
    {
        var quiet = FanCurves.For(AutoTdpProtocol.QuietFans)!;
        var cool = FanCurves.For(AutoTdpProtocol.CoolFans)!;
        var three = FanCurves.Blend(3);

        Assert.Equal(quiet[..8], three[..8]);
        for (var i = 8; i < 16; i++)
        {
            Assert.InRange(three[i], Math.Min(quiet[i], cool[i]), Math.Max(quiet[i], cool[i]));
        }

        Assert.NotEqual(quiet, three);
        Assert.NotEqual(cool, three);
    }

    [Fact]
    public void A_step_past_the_ends_is_kept_within_them() =>
        Assert.Equal(FanCurves.Blend(10), FanCurves.Blend(14));

    // ----- Which step -----

    [Theory]
    [InlineData(60, 0)]
    [InlineData(50, 0)]
    [InlineData(70, 6)]
    [InlineData(77, 10)]
    [InlineData(85, 10)]
    public void The_temperature_sets_the_step(int degrees, int step) =>
        Assert.Equal(step, new FineFans().Pick(Start, degrees, 10, Top, Limit, boost: false));

    [Fact]
    public void High_power_wants_step_5() =>
        Assert.Equal(5, new FineFans().Pick(Start, 50, 21, Top, Limit, boost: false));

    [Fact]
    public void A_power_jump_adds_2_steps()
    {
        var fans = new FineFans();
        Assert.Equal(0, fans.Pick(Start, 50, 8, Top, Limit, boost: false));
        Assert.Equal(2, fans.Pick(Start.AddSeconds(5), 50, 14, Top, Limit, boost: false));
    }

    [Fact]
    public void Boost_turning_on_adds_2_steps_and_holds_4()
    {
        var fans = new FineFans();
        Assert.Equal(0, fans.Pick(Start, 50, 10, Top, Limit, boost: false));
        Assert.Equal(4, fans.Pick(Start.AddSeconds(1), 50, 10, Top, Limit, boost: true));

        // Still on a minute later, and cool: never under 4.
        Assert.Equal(4, fans.Pick(Start.AddSeconds(90), 50, 10, Top, Limit, boost: true));
    }

    [Fact]
    public void Boost_turning_on_from_a_higher_step_adds_2()
    {
        var fans = new FineFans();
        Assert.Equal(6, fans.Pick(Start, 70, 10, Top, Limit, boost: false));
        Assert.Equal(8, fans.Pick(Start.AddSeconds(1), 70, 10, Top, Limit, boost: true));
    }

    [Fact]
    public void It_steps_up_at_once_and_down_one_step_per_15_seconds()
    {
        var fans = new FineFans();
        Assert.Equal(10, fans.Pick(Start, 85, 10, Top, Limit, boost: false));

        // Cool at once: 14 seconds is not enough, 15 is one step, and each step after takes another 15.
        Assert.Equal(10, fans.Pick(Start.AddSeconds(1), 50, 10, Top, Limit, boost: false));
        Assert.Equal(10, fans.Pick(Start.AddSeconds(15), 50, 10, Top, Limit, boost: false));
        Assert.Equal(9, fans.Pick(Start.AddSeconds(16), 50, 10, Top, Limit, boost: false));
        Assert.Equal(9, fans.Pick(Start.AddSeconds(30), 50, 10, Top, Limit, boost: false));
        Assert.Equal(8, fans.Pick(Start.AddSeconds(31), 50, 10, Top, Limit, boost: false));

        // Hot again: straight back up.
        Assert.Equal(10, fans.Pick(Start.AddSeconds(32), 85, 10, Top, Limit, boost: false));
    }

    [Fact]
    public void No_temperature_starts_at_5_and_then_keeps_the_step()
    {
        var fans = new FineFans();
        Assert.Equal(5, fans.Pick(Start, null, 10, Top, Limit, boost: false));
        Assert.Equal(10, fans.Pick(Start.AddSeconds(1), 85, 10, Top, Limit, boost: false));
        Assert.Equal(10, fans.Pick(Start.AddSeconds(60), null, 10, Top, Limit, boost: false));
    }
}
