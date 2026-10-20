using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public class DynamicFansTests
{
    private const int Limit = 80;
    private const int MaxWatts = 25;
    private static readonly DateTime Start = new(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => Start.AddSeconds(seconds);

    [Theory]
    [InlineData(50, AutoTdpProtocol.QuietFans)]
    [InlineData(69, AutoTdpProtocol.QuietFans)]
    [InlineData(70, AutoTdpProtocol.CoolFans)]
    [InlineData(76, AutoTdpProtocol.CoolFans)]
    [InlineData(77, AutoTdpProtocol.FullFans)]
    [InlineData(85, AutoTdpProtocol.FullFans)]
    public void The_temperature_picks_the_curve_against_the_limit(int temperature, string expected) =>
        Assert.Equal(expected, new DynamicFans().Pick(Start, temperature, null, 0, Limit));

    [Fact]
    public void The_thresholds_follow_a_lower_limit()
    {
        Assert.Equal(AutoTdpProtocol.CoolFans, new DynamicFans().Pick(Start, 60, null, 0, 70));
        Assert.Equal(AutoTdpProtocol.FullFans, new DynamicFans().Pick(Start, 67, null, 0, 70));
    }

    [Fact]
    public void It_steps_up_at_once()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 60, null, 0, Limit);

        Assert.Equal(AutoTdpProtocol.FullFans, fans.Pick(At(1), 78, null, 0, Limit));
    }

    [Fact]
    public void It_steps_down_only_after_30_seconds_cooler()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 72, null, 0, Limit);

        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(1), 60, null, 0, Limit));
        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(30), 60, null, 0, Limit));
        Assert.Equal(AutoTdpProtocol.QuietFans, fans.Pick(At(31), 60, null, 0, Limit));
    }

    [Fact]
    public void Warming_again_restarts_the_wait()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 72, null, 0, Limit);
        fans.Pick(At(1), 60, null, 0, Limit);
        fans.Pick(At(20), 71, null, 0, Limit);

        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(40), 60, null, 0, Limit));
        Assert.Equal(AutoTdpProtocol.QuietFans, fans.Pick(At(71), 60, null, 0, Limit));
    }

    [Fact]
    public void High_power_keeps_it_on_cool_at_least()
    {
        // 70% of 25 W is 17.5, so 18 W is high and 17 W is not.
        Assert.Equal(AutoTdpProtocol.CoolFans, new DynamicFans().Pick(Start, 50, 18, MaxWatts, Limit));
        Assert.Equal(AutoTdpProtocol.QuietFans, new DynamicFans().Pick(Start, 50, 17, MaxWatts, Limit));
    }

    [Fact]
    public void A_power_jump_steps_up_one_before_the_heat_arrives()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 55, 8, MaxWatts, Limit);

        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(5), 55, 13, MaxWatts, Limit));
    }

    [Fact]
    public void A_slow_rise_in_power_is_not_a_jump()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 55, 8, MaxWatts, Limit);
        fans.Pick(At(6), 55, 10, MaxWatts, Limit);
        fans.Pick(At(12), 55, 12, MaxWatts, Limit);

        Assert.Equal(AutoTdpProtocol.QuietFans, fans.Pick(At(18), 55, 13, MaxWatts, Limit));
    }

    [Fact]
    public void A_jump_is_held_like_heat()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 55, 8, MaxWatts, Limit);
        fans.Pick(At(5), 55, 13, MaxWatts, Limit);

        // The jump has left the 10 second window, so only the 30 second hold keeps it on Cool.
        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(20), 55, 13, MaxWatts, Limit));
        Assert.Equal(AutoTdpProtocol.QuietFans, fans.Pick(At(51), 55, 13, MaxWatts, Limit));
    }

    [Fact]
    public void Without_a_temperature_it_keeps_what_it_has()
    {
        var fans = new DynamicFans();
        fans.Pick(At(0), 72, null, 0, Limit);

        Assert.Equal(AutoTdpProtocol.CoolFans, fans.Pick(At(60), null, null, 0, Limit));
    }

    [Fact]
    public void Without_any_reading_it_starts_on_cool() =>
        Assert.Equal(AutoTdpProtocol.CoolFans, new DynamicFans().Pick(Start, null, null, 0, Limit));

    [Fact]
    public void Dynamic_is_offered_but_has_no_fixed_curve()
    {
        Assert.Contains(AutoTdpProtocol.DynamicFans, AutoTdpProtocol.FanModes);
        Assert.DoesNotContain(AutoTdpProtocol.DynamicFans, AutoTdpProtocol.FanCurves);
        Assert.Null(FanCurves.For(AutoTdpProtocol.DynamicFans));
    }
}
