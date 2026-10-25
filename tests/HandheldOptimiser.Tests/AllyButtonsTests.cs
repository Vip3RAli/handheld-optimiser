using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>How the Ally's front-panel button reports are read, and how a bouncing press is filtered.</summary>
public class AllyButtonsTests
{
    [Theory]
    [InlineData(new byte[] { 0x5A, 0x38, 0, 0, 0, 0 }, "ArmouryCrate")]
    [InlineData(new byte[] { 0x5A, 0xA6, 0, 0, 0, 0 }, "CommandCenter")]
    [InlineData(new byte[] { 0x5A, 0xA5, 0, 0, 0, 0 }, "Paddle")]
    [InlineData(new byte[] { 0x5A, 0x00, 0, 0, 0, 0 }, "Release")]
    [InlineData(new byte[] { 0x5A, 0xEC, 0, 0, 0, 0 }, "None")]
    [InlineData(new byte[] { 0x5B, 0x38, 0, 0, 0, 0 }, "None")]
    [InlineData(new byte[] { 0x5A }, "None")]
    [InlineData(new byte[] { }, "None")]
    public void Each_report_is_read_as_its_button(byte[] report, string expected) =>
        Assert.Equal(expected, AllyButtons.Parse(report).ToString());

    [Fact]
    public void The_same_button_again_within_300_ms_is_ignored()
    {
        var filter = new AllyButtonFilter();
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        byte[] ac = [0x5A, 0x38, 0, 0, 0, 0];
        Assert.Equal(AllyButton.ArmouryCrate, filter.Press(ac, t0));
        Assert.Null(filter.Press(ac, t0.AddMilliseconds(200)));
        Assert.Equal(AllyButton.ArmouryCrate, filter.Press(ac, t0.AddMilliseconds(400)));
    }

    [Fact]
    public void A_different_button_soon_after_still_counts()
    {
        var filter = new AllyButtonFilter();
        var t0 = DateTime.UtcNow;
        filter.Press([0x5A, 0x38, 0, 0, 0, 0], t0);
        Assert.Equal(AllyButton.CommandCenter, filter.Press([0x5A, 0xA6, 0, 0, 0, 0], t0.AddMilliseconds(50)));
    }

    [Fact]
    public void Releases_and_unknown_codes_are_not_presses()
    {
        var filter = new AllyButtonFilter();
        Assert.Null(filter.Press([0x5A, 0x00, 0, 0, 0, 0], DateTime.UtcNow));
        Assert.Null(filter.Press([0x5A, 0xEC, 0, 0, 0, 0], DateTime.UtcNow));
    }

    [Fact]
    public void A_button_already_read_is_filtered_the_same_as_its_report()
    {
        var filter = new AllyButtonFilter();
        var t0 = DateTime.UtcNow;
        Assert.Equal(AllyButton.ArmouryCrate, filter.Press(AllyButton.ArmouryCrate, t0));
        Assert.Null(filter.Press([0x5A, 0x38, 0, 0, 0, 0], t0.AddMilliseconds(100)));
        Assert.Null(filter.Press(AllyButton.None, t0));
        Assert.Null(filter.Press(AllyButton.Release, t0));
    }
}
