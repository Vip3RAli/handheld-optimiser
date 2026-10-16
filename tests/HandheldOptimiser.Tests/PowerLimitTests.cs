using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public class PowerLimitTests
{
    private static readonly AutoTdpSettings Library = new(TargetFps: 60, MinWatts: 7, MaxWatts: 30, TemperatureLimit: 80);

    [Fact]
    public void Auto_keeps_the_range_it_was_given() =>
        Assert.Equal(Library, PowerLimits.WithTop(Library, 0));

    [Fact]
    public void A_power_limit_with_a_target_is_the_top_of_the_range() =>
        Assert.Equal(Library with { MaxWatts = 15 }, PowerLimits.WithTop(Library, 15));

    [Fact]
    public void The_top_never_goes_under_the_bottom() =>
        Assert.Equal(Library with { MinWatts = 12, MaxWatts = 12 }, PowerLimits.WithTop(Library with { MinWatts = 12 }, 10));

    [Fact]
    public void The_top_never_goes_over_the_highest_allowed() =>
        Assert.Equal(AutoTdpProtocol.HighestWatts, PowerLimits.WithTop(Library, 45).MaxWatts);

    [Theory]
    [InlineData(0, 15, 15)]
    [InlineData(0, 3, AutoTdpProtocol.LowestWatts)]
    [InlineData(0, 45, AutoTdpProtocol.HighestWatts)]
    [InlineData(0, 0, null)]
    [InlineData(60, 15, null)]
    public void A_power_limit_without_a_target_is_fixed(int targetFps, int powerLimit, int? expected) =>
        Assert.Equal(expected, PowerLimits.Fixed(targetFps, powerLimit));
}
