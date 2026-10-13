using System.Windows.Input;

namespace HandheldOptimiser.Views;

/// <summary>The keys that give the keyboard back to a control when none has it.</summary>
public static class FocusKeys
{
    /// <summary>
    /// Whether a key, pressed while nothing has the keyboard, should put it on a control. Arrow keys
    /// and Tab move from whichever control has the keyboard, so with none they would do nothing: a
    /// D-pad would be dead after a touch on a blank part of the page. Only keys that move about do
    /// this. Enter is left out on purpose, so that it never presses something nobody chose.
    /// </summary>
    public static bool Wakes(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab;
}
