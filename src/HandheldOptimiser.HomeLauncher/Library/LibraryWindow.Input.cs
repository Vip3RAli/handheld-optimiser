using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Moving through the grid and acting on the controller, keyboard, mouse and touch.</summary>
public partial class LibraryWindow
{
    /// <summary>
    /// Moves through the grid by row and column. WPF's own directional navigation only moves to a tile
    /// directly in line, so Down did nothing from a column past the end of a shorter last row.
    /// </summary>
    private void OnNavigate(FocusNavigationDirection direction)
    {
        if (_editing is not null)
        {
            return;
        }

        if (_menu is not null)
        {
            MoveMenuFocus(direction);
            return;
        }

        if (Keyboard.FocusedElement is not Button { DataContext: GameTile tile })
        {
            RestoreFocus();
            return;
        }

        var index = _tiles.IndexOf(tile);
        var columns = ColumnCount();
        var last = _tiles.Count - 1;

        var target = direction switch
        {
            FocusNavigationDirection.Left => index - 1,
            FocusNavigationDirection.Right => index + 1,
            FocusNavigationDirection.Up => index - columns,
            // From a column the shorter last row does not reach, land on its last game.
            FocusNavigationDirection.Down when index / columns < last / columns => Math.Min(index + columns, last),
            _ => -1
        };

        if (target >= 0 && target <= last)
        {
            FocusTile(target);
        }
    }

    /// <summary>
    /// Tiles in the first row, which is the column count since every tile is the same width. Measured on
    /// the item containers: the focused button is scaled up, which shifts its own position.
    /// </summary>
    private int ColumnCount()
    {
        double? firstTop = null;
        var columns = 0;

        while (columns < _tiles.Count && TileContainer(columns) is { } container)
        {
            var top = container.TranslatePoint(default, Tiles).Y;
            firstTop ??= top;
            if (Math.Abs(top - firstTop.Value) >= 1)
            {
                break;
            }

            columns++;
        }

        return Math.Max(1, columns);
    }

    private void FocusTile(int index)
    {
        if (TileContainer(index) is { } container)
        {
            VisualChild<Button>(container)?.Focus();
        }
    }

    private ContentPresenter? TileContainer(int index) =>
        Tiles.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

    private void OnGamepadPressed(GamepadAction action)
    {
        if (_editing is not null)
        {
            // The Windows touch keyboard takes A, B, X and Y for typing, so those must not act here too.
            switch (action)
            {
                case GamepadAction.Menu:
                    SaveEdit();
                    break;
                case GamepadAction.View:
                    ShowMenuItems();
                    break;
            }

            return;
        }

        if (_menu is not null)
        {
            switch (action)
            {
                case GamepadAction.Accept when Keyboard.FocusedElement is Button item && _menu.Children.Contains(item):
                    item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case GamepadAction.Back:
                    if (!BackToSettings())
                    {
                        CloseMenu();
                    }

                    break;
            }

            return;
        }

        switch (action)
        {
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                Launch(tile);
                break;
            case GamepadAction.Options when _quickActions && Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                OpenMenu(tile);
                break;
            case GamepadAction.Refresh:
                _ = ScanAsync();
                break;
            case GamepadAction.Menu:
                OpenSettings();
                break;
            case GamepadAction.View:
                OpenPower();
                break;
            case GamepadAction.PreviousFilter:
                CycleFilter(-1);
                break;
            case GamepadAction.NextFilter:
                CycleFilter(1);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_editing is not null)
        {
            // Every other key belongs to the text box.
            if (e.Key == Key.Enter)
            {
                SaveEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ShowMenuItems();
                e.Handled = true;
            }

            return;
        }

        FocusNavigationDirection? direction = e.Key switch
        {
            Key.Left => FocusNavigationDirection.Left,
            Key.Right => FocusNavigationDirection.Right,
            Key.Up => FocusNavigationDirection.Up,
            Key.Down => FocusNavigationDirection.Down,
            _ => null
        };

        if (direction is { } d)
        {
            // Same grid movement as the controller, instead of WPF's in-line-only navigation.
            OnNavigate(d);
            e.Handled = true;
        }
        else if (_menu is not null)
        {
            if (e.Key == Key.Escape)
            {
                if (!BackToSettings())
                {
                    CloseMenu();
                }

                e.Handled = true;
            }
        }
        else if (e.Key == Key.F5)
        {
            _ = ScanAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            OpenSettings();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            OpenPower();
            e.Handled = true;
        }
        else if (e.Key is Key.PageUp or Key.PageDown)
        {
            CycleFilter(e.Key == Key.PageUp ? -1 : 1);
            e.Handled = true;
        }
        else if (_quickActions && e.Key is Key.X or Key.Apps && Keyboard.FocusedElement is Button { DataContext: GameTile tile })
        {
            OpenMenu(tile);
            e.Handled = true;
        }
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile })
        {
            Launch(tile);
        }
    }

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_quickActions && sender is Button { DataContext: GameTile tile })
        {
            OpenMenu(tile);
            e.Handled = true;
        }
    }

    private void OnTileFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile } button)
        {
            _focusedKey = tile.Game.Key;
            QueueBackdrop(tile);

            // Leave room for the focus ring and the title underneath when scrolling a row into view.
            button.BringIntoView(new Rect(-20, -40, button.ActualWidth + 40, button.ActualHeight + 80));
        }
    }

    private static T? VisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (VisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
