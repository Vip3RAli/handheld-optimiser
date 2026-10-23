using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Finding things in the visual tree, and the buttons an items control makes for its items.</summary>
internal static class Visuals
{
    /// <summary>The first element of a type below another, depth first, or null.</summary>
    public static T? Child<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (Child<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Hands the clicks, focus and right clicks of the buttons an items control makes for its items to
    /// handlers that take the button as their sender. The item templates are in LibraryResources.xaml,
    /// which has no code-behind to name handlers in, so they are caught here as they bubble up to the list.
    /// </summary>
    public static void OnItemButtons(ItemsControl items, RoutedEventHandler click, KeyboardFocusChangedEventHandler focused,
        MouseButtonEventHandler? rightClick = null)
    {
        // A button raises Click and takes the focus itself, so it is the original source of both.
        items.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button button)
            {
                click(button, e);
            }
        }));

        items.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button button)
            {
                focused(button, e);
            }
        }));

        if (rightClick is null)
        {
            return;
        }

        // A right click lands on whatever is drawn inside the button, so the button is found above it.
        items.AddHandler(UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            for (var at = e.OriginalSource as DependencyObject; at is not null && !ReferenceEquals(at, items);
                 at = VisualTreeHelper.GetParent(at))
            {
                if (at is Button button)
                {
                    rightClick(button, e);
                    return;
                }
            }
        }));
    }
}
