using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>What the Armoury Crate button does: brings the library forward, or, pressed again, sends the player back.</summary>
public class ButtonPressTests
{
    [Theory]
    [InlineData(false, false, "ShowLibrary")]
    [InlineData(false, true, "ShowLibrary")]
    [InlineData(true, true, "BackToGame")]
    [InlineData(true, false, "Minimise")]
    public void A_press_brings_the_library_forward_or_sends_the_player_back(bool libraryInFront, bool gameRunning, string expected) =>
        Assert.Equal(expected, ButtonPress.Decide(libraryInFront, gameRunning, fullScreenExperience: false).ToString());

    /// <summary>Xbox mode has no desktop behind the library, so minimising it left a blank screen (seen on the Ally).</summary>
    [Theory]
    [InlineData(false, false, "ShowLibrary")]
    [InlineData(false, true, "ShowLibrary")]
    [InlineData(true, true, "BackToGame")]
    [InlineData(true, false, "Stay")]
    public void In_Xbox_mode_the_library_is_never_minimised(bool libraryInFront, bool gameRunning, string expected) =>
        Assert.Equal(expected, ButtonPress.Decide(libraryInFront, gameRunning, fullScreenExperience: true).ToString());

    [Theory]
    [InlineData(false, "Minimise")]
    [InlineData(true, "Stay")]
    public void A_game_that_will_not_come_back_minimises_the_library_only_on_the_desktop(bool fullScreenExperience, string expected) =>
        Assert.Equal(expected, ButtonPress.WhenGameWillNotReturn(fullScreenExperience).ToString());
}
