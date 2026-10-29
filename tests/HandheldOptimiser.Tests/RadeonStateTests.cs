using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>How the launcher's --radeon mode prints the Radeon settings, and how TDP Engine reads them.</summary>
public class RadeonStateTests
{
    private static readonly RadeonState Everything = new()
    {
        Rsr = true,
        RsrSharpness = 75,
        RsrSharpnessRange = new RadeonRange(0, 100, 1),
        Afmf = false,
        AntiLag = true,
        Chill = false,
        ChillMin = 30,
        ChillMax = 60,
        ChillRange = new RadeonRange(30, 300, 1),
        Sharpening = true,
        SharpeningStrength = 80,
        SharpeningRange = new RadeonRange(10, 100, 1),
    };

    [Fact]
    public void Every_setting_comes_back_as_it_went() =>
        Assert.Equal(Everything, RadeonState.Parse(Everything.Format()));

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    public void An_empty_line_is_a_driver_that_offers_none(string ending) =>
        Assert.Equal(RadeonState.Unknown, RadeonState.Parse(RadeonState.Unknown.Format() + ending));

    [Fact]
    public void Settings_not_known_are_left_out_of_the_line() =>
        Assert.Equal("rsr=1 afmf=0", new RadeonState { Rsr = true, Afmf = false }.Format());

    [Fact]
    public void No_output_says_nothing() =>
        Assert.Null(RadeonState.Parse(null));

    [Fact]
    public void Pairs_it_does_not_know_or_cannot_read_are_passed_over() =>
        Assert.Equal(
            new RadeonState { Rsr = true },
            RadeonState.Parse("rsr=1 boost=1 afmf=yes chill-min=x chill-range=60,30,1 nonsense\r\n"));

    [Theory]
    [InlineData(5, 30)]
    [InlineData(45, 45)]
    [InlineData(500, 300)]
    public void A_number_is_brought_inside_the_range(int asked, int set) =>
        Assert.Equal(set, new RadeonRange(30, 300, 1).Fit(asked));

    [Theory]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(99, 100)]
    public void A_number_is_put_on_the_nearest_step(int asked, int set) =>
        Assert.Equal(set, new RadeonRange(0, 100, 5).Fit(asked));

    [Fact]
    public void Every_switch_takes_on_or_off_and_every_number_a_number()
    {
        Assert.All(RadeonState.Switches, name => Assert.True(RadeonState.IsChange(name, true, null)));
        Assert.All(RadeonState.Switches, name => Assert.False(RadeonState.IsChange(name, null, 40)));
        Assert.All(RadeonState.Numbers, name => Assert.True(RadeonState.IsChange(name, null, 40)));
        Assert.All(RadeonState.Numbers, name => Assert.False(RadeonState.IsChange(name, true, null)));
        Assert.False(RadeonState.IsChange("boost", true, null));
        Assert.False(RadeonState.IsChange(null, true, null));
    }
}
