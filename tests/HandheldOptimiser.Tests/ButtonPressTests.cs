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
        Assert.Equal(expected, ButtonPress.Decide(libraryInFront, gameRunning).ToString());
}
