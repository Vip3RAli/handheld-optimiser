using System.Windows.Input;

namespace HandheldOptimiser.Views;

/// <summary>The keys that take a goal's page back to the Dashboard.</summary>
public static class BackKeys
{
    /// <summary>
    /// Whether a key press means Back: Escape, Alt+Left or the keyboard's own Back key. Never while a
    /// text box has the keyboard, where Escape leaving the page would throw away what was being typed.
    /// </summary>
    public static bool GoesBack(Key key, ModifierKeys modifiers, bool typingInTextBox)
    {
        if (typingInTextBox)
        {
            return false;
        }

        return key switch
        {
            Key.Escape or Key.BrowserBack => modifiers == ModifierKeys.None,
            Key.Left => modifiers == ModifierKeys.Alt,
            _ => false
        };
    }
}
