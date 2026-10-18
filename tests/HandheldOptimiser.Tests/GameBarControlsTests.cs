using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Game Bar is worked with the controller as a gamepad, so in Desktop mode the controller is a gamepad
/// for as long as Game Bar is open, and then back to the mode the player saved.
/// </summary>
public class GameBarControlsTests
{
    [Fact]
    public void Desktop_mode_is_held_as_a_gamepad_while_Game_Bar_is_open() =>
        Assert.Equal(AutoTdpProtocol.GamepadMode, GameBarControls.ModeWhileOpen(
            new AutoTdpStatus(true, Replacing: true, ControllerMode: AutoTdpProtocol.DesktopMode)));

    [Fact]
    public void Gamepad_mode_is_left_as_it_is() =>
        Assert.Null(GameBarControls.ModeWhileOpen(
            new AutoTdpStatus(true, Replacing: true, ControllerMode: AutoTdpProtocol.GamepadMode)));

    [Fact]
    public void Nothing_changes_when_Armoury_Crate_is_not_replaced() =>
        Assert.Null(GameBarControls.ModeWhileOpen(
            new AutoTdpStatus(true, Replacing: false, ControllerMode: AutoTdpProtocol.DesktopMode)));

    [Fact]
    public void Nothing_changes_when_TDP_Engine_did_not_answer() =>
        Assert.Null(GameBarControls.ModeWhileOpen(null));
}
