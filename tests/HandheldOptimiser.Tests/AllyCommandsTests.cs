using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>The bytes sent to the Ally's controller, and how long to wait between looks for it.</summary>
public class AllyCommandsTests
{
    [Fact]
    public void Wake_is_the_report_id_then_ASUS_Tech_Inc_padded_to_64()
    {
        var bytes = AllyCommands.Wake();
        Assert.Equal(64, bytes.Length);
        Assert.Equal(new byte[] { 0x5A, 0x41, 0x53, 0x55, 0x53, 0x20, 0x54, 0x65, 0x63, 0x68, 0x2E, 0x49, 0x6E, 0x63, 0x2E }, bytes[..15]);
        Assert.All(bytes[15..], b => Assert.Equal(0, b));
    }

    // The mode is an int here because the enum is internal and a public test method cannot take it.
    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(3, 0x03)]
    public void Mode_is_5A_D1_01_01_then_the_mode(int mode, byte value)
    {
        var bytes = AllyCommands.Mode((ControllerMode)mode);
        Assert.Equal(64, bytes.Length);
        Assert.Equal(new byte[] { 0x5A, 0xD1, 0x01, 0x01, value }, bytes[..5]);
        Assert.All(bytes[5..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("gamepad", 1)]
    [InlineData("Desktop", 3)]
    [InlineData("mouse", null)]
    [InlineData(null, null)]
    public void Mode_names_are_read(string? name, int? expected) =>
        Assert.Equal(expected, (int?)AllyCommands.ParseMode(name));

    [Theory]
    [InlineData(1, "gamepad")]
    [InlineData(3, "desktop")]
    public void Mode_names_are_written_in_lower_case(int mode, string expected) =>
        Assert.Equal(expected, AllyCommands.ModeName((ControllerMode)mode));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(50, 30)]
    public void Looking_for_the_controller_waits_longer_each_time_up_to_30_seconds(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AllyCommands.RetryDelay(attempt));

    /// <summary>
    /// While Game Bar holds the controller as a gamepad, a mode chosen in the widget is saved but not sent:
    /// in Desktop mode LB is Tab and RB a click, which walk Game Bar's lists and close it.
    /// </summary>
    [Theory]
    [InlineData(3, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(3, null, 3)]
    [InlineData(1, null, 1)]
    [InlineData(null, 1, 1)]
    [InlineData(null, null, null)]
    public void A_hold_wins_over_the_saved_mode_until_it_is_released(int? saved, int? hold, int? expected) =>
        Assert.Equal(expected, (int?)AllyCommands.ModeToSend((ControllerMode?)saved, (ControllerMode?)hold));
}
