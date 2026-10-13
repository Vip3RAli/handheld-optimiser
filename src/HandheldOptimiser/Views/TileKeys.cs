using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.Views;

/// <summary>Where an arrow key pressed in a tile takes the keyboard.</summary>
public enum TileKeyMove
{
    /// <summary>Left to WPF, which moves to the next control in that direction.</summary>
    None,

    /// <summary>In to the tile's own switch or tick box.</summary>
    ToInner,

    /// <summary>Back out to the tile.</summary>
    ToTile,

    /// <summary>Out of the tile in the arrow's direction, passing over the tile's own switch.</summary>
    Out
}

/// <summary>
/// Arrow keys inside a tile. A controller reaches this app as arrow keys, Enter and Escape, so this is
/// what makes a tile and its switch both reachable from a D-pad. The switch sits inside the tile, and
/// WPF's own arrow key movement, asked to move from a tile, finds that tile's own switch first,
/// whichever arrow was pressed. Left would bounce between the two for ever, and Up and Down would
/// take two presses a row. So on a tile, Right steps
/// in to its switch and the other three arrows leave the tile, passing over the switch. On the
/// switch, Left steps back out to the tile, and the rest is left to WPF, which finds the next tile.
/// Enter then presses whichever has the keyboard: the tile, to open what it does, or the switch, to
/// flip it.
/// </summary>
public static class TileKeys
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(TileKeys),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// <summary>The rule, apart from the controls it moves between so that it can be tested.</summary>
    /// <param name="focusOnTile">Whether the tile itself has the keyboard, and not the switch inside it.</param>
    /// <param name="hasInner">Whether the tile has a switch or tick box that can take the keyboard now.</param>
    public static TileKeyMove Move(Key key, bool focusOnTile, bool hasInner) => key switch
    {
        Key.Right when focusOnTile && hasInner => TileKeyMove.ToInner,
        Key.Left or Key.Up or Key.Down when focusOnTile && hasInner => TileKeyMove.Out,
        Key.Left when !focusOnTile => TileKeyMove.ToTile,
        _ => TileKeyMove.None
    };

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase tile)
        {
            return;
        }

        tile.PreviewKeyDown -= OnPreviewKeyDown;

        if ((bool)e.NewValue)
        {
            tile.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ButtonBase tile || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var inner = Inner(tile);

        switch (Move(e.Key, ReferenceEquals(e.OriginalSource, tile), inner is not null))
        {
            case TileKeyMove.ToInner:
                e.Handled = inner!.Focus();
                break;

            case TileKeyMove.ToTile:
                e.Handled = tile.Focus();
                break;

            case TileKeyMove.Out:
                // Asked of the switch, WPF looks beyond the tile. Where it finds another tile's switch,
                // the keyboard goes to that tile: moving about lands on tiles, and Right steps in.
                var direction = e.Key switch
                {
                    Key.Left => FocusNavigationDirection.Left,
                    Key.Up => FocusNavigationDirection.Up,
                    _ => FocusNavigationDirection.Down
                };

                if (inner!.PredictFocus(direction) is UIElement found)
                {
                    var target = TileOf(found) ?? found;

                    if (!ReferenceEquals(target, tile))
                    {
                        target.Focus();
                    }
                }

                // With nowhere to go, the key is still spent: left to WPF it would step in to the switch.
                e.Handled = true;
                break;
        }
    }

    /// <summary>The tile a control sits in, if it is a switch or tick box inside one.</summary>
    private static ButtonBase? TileOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ButtonBase tile && GetEnabled(tile))
            {
                return tile;
            }
        }

        return null;
    }

    /// <summary>The tile's switch or tick box, if it has one that can take the keyboard now.</summary>
    private static ToggleButton? Inner(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is ToggleButton { IsVisible: true, IsEnabled: true, Focusable: true } found)
            {
                return found;
            }

            if (Inner(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
