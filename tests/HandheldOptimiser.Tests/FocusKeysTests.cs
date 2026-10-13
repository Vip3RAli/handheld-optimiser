using System.Windows.Input;
using HandheldOptimiser.Views;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Arrow keys move from whichever control has the keyboard. When none has it (a blank part of the
/// page was touched, say) they would do nothing at all, so the first one pressed gives the keyboard
/// back to a control. Only keys that move about do this: Enter with nothing chosen must not press anything.
/// </summary>
public sealed class FocusKeysTests
{
    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Tab)]
    public void A_key_that_moves_about_wakes_the_keyboard(Key key) => Assert.True(FocusKeys.Wakes(key));

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    [InlineData(Key.A)]
    public void Any_other_key_does_not(Key key) => Assert.False(FocusKeys.Wakes(key));
}
