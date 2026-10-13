using System.Windows.Input;
using HandheldOptimiser.Views;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Which key presses take a goal's page back to the Dashboard. Escape must not while a text box has
/// the keyboard: on the Full Screen page that would throw away a path half typed.
/// </summary>
public sealed class BackKeysTests
{
    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None, false, true)]
    [InlineData(Key.BrowserBack, ModifierKeys.None, false, true)]
    [InlineData(Key.Left, ModifierKeys.Alt, false, true)]
    [InlineData(Key.Left, ModifierKeys.None, false, false)]
    [InlineData(Key.Escape, ModifierKeys.None, true, false)]
    [InlineData(Key.Back, ModifierKeys.None, false, false)]
    public void Which_keys_go_back(Key key, ModifierKeys modifiers, bool typing, bool expected) =>
        Assert.Equal(expected, BackKeys.GoesBack(key, modifiers, typing));
}
