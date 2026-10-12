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
        if (_editing is not null || _cardGrowing)
        {
            return;
        }

        // The d-pad moves around Windows' keyboard while the search box is being typed in.
        if (IsSearching)
        {
            return;
        }

        if (_menu is not null)
        {
            MoveMenuFocus(direction);
            return;
        }

        // In the Old School layout up and down are between the categories, whatever has the focus, and
        // left and right along the cards of the one that is open.
        if (IsOldSchool && direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down)
        {
            StepCategory(direction == FocusNavigationDirection.Up ? 1 : -1);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: ActionCard })
        {
            NavigateCards(ActionStack, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button topBar && IsTopBarButton(topBar))
        {
            NavigateTopBar(topBar, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: DealTile } deal)
        {
            NavigateDeals(deal, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: DealSourceTile } source)
        {
            NavigateDealSources(source, direction);
            return;
        }

        if (Keyboard.FocusedElement is not Button { DataContext: GameTile tile } button)
        {
            // With nothing to show, such as an empty Apps tab, the top bar is all there is to reach.
            if (_tiles.Count == 0 && direction == FocusNavigationDirection.Up)
            {
                FocusTopBar();
            }
            else
            {
                RestoreFocus();
            }

            return;
        }

        if (InContinueRow(button))
        {
            NavigateContinue(direction);
            return;
        }

        var index = _tiles.IndexOf(tile);
        var columns = ColumnCount();
        var last = _tiles.Count - 1;

        // Up from the grid's first row goes to the Last played card above it, or to the top bar.
        if (direction == FocusNavigationDirection.Up && index >= 0 && index < columns)
        {
            if (!FocusContinueAbove())
            {
                FocusTopBar();
            }

            return;
        }

        // Down from the grid's last row goes to the Deals row below it.
        if (direction == FocusNavigationDirection.Down && index >= 0 && index / columns == last / columns && FocusDealBelow(button))
        {
            return;
        }

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
    /// the item containers: the focused button is scaled up, which shifts its own position. The row
    /// and the Old School cards are one line of every tile.
    /// </summary>
    private int ColumnCount()
    {
        if (IsOneLine)
        {
            return Math.Max(1, _tiles.Count);
        }

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
        // The Not installed card is on its way open, and what the press was meant for is on its way out.
        if (_cardGrowing)
        {
            return;
        }

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

        if (IsSearching)
        {
            // As in the text editor, the letters are typed with A, X and Y on Windows' keyboard.
            switch (action)
            {
                case GamepadAction.Menu:
                    FinishSearch();
                    break;
                case GamepadAction.View:
                    ClearTyping();
                    break;

                // B puts Windows' keyboard away, and with it the cursor: the bar is picked out again with
                // what was typed kept, and B once more closes the search.
                case GamepadAction.Back:
                    EndTyping();
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

                // Y opens quick settings and closes them again, like Armoury Crate's Command Center button.
                case GamepadAction.QuickSettings when ReferenceEquals(_menu, QuickItems):
                    CloseMenu();
                    break;
            }

            return;
        }

        switch (action)
        {
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                Launch(tile);
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: DealTile deal }:
                OpenDeal(deal);
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: DealSourceTile } source:
                source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: ActionCard card }:
                card.Act();
                break;

            // B in the Old School layout's open Not installed card closes it.
            case GamepadAction.Back when !IsSearchOpen && LeaveNotInstalled():
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button topBar && IsTopBarButton(topBar):
                // The search button opens differently for the controller than for a tap.
                _gamepadPress = true;
                try
                {
                    topBar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally
                {
                    _gamepadPress = false;
                }

                break;
            case GamepadAction.Options when _quickActions && Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                OpenMenu(tile);
                break;

            // B among the games a search found closes it and shows them all again.
            case GamepadAction.Back when IsSearchOpen:
                ClearSearch();
                break;
            case GamepadAction.Menu:
                OpenSettingsOrItsCards();
                break;
            case GamepadAction.View:
                OpenPowerOrItsCards();
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
        if (_cardGrowing)
        {
            e.Handled = true;
            return;
        }

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

        if (IsSearching)
        {
            // Every other key belongs to the search box.
            if (e.Key is Key.Enter or Key.Down)
            {
                FinishSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndTyping();
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
        else if (e.Key == Key.Escape && IsSearchOpen)
        {
            ClearSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && LeaveNotInstalled())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = ScanAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            OpenSettingsOrItsCards();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            OpenPowerOrItsCards();
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
            // A tap on an Old School card behind the front one only brings it forward, which taking the
            // focus has just done. The next tap, on what is now the front card, starts its game.
            if (CardClickSwallowed(GameCards, tile))
            {
                return;
            }

            Launch(tile);
        }
    }

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_quickActions && sender is Button { DataContext: GameTile tile } button)
        {
            // An Old School card turns over to show its quick actions, which it does at the front. One
            // behind the front comes forward first, and the next press and hold turns it.
            if (IsOldSchool && _tiles.IndexOf(tile) != GameCards.Front)
            {
                button.Focus();
            }
            else
            {
                OpenMenu(tile);
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// The Menu button: the settings, which in the Old School layout are the cards of their own category.
    /// From the open Not installed card that closes it first.
    /// </summary>
    private void OpenSettingsOrItsCards()
    {
        if (_layout == LibraryLayout.OldSchool)
        {
            LeaveNotInstalled();
            OpenCategoryOf(CategoryKind.Settings);
        }
        else
        {
            OpenSettings();
        }
    }

    /// <summary>The View button: the power menu, which in the Old School layout is the cards of its own category.</summary>
    private void OpenPowerOrItsCards()
    {
        if (_layout == LibraryLayout.OldSchool)
        {
            LeaveNotInstalled();
            OpenCategoryOf(CategoryKind.Power);
        }
        else
        {
            OpenPower();
        }
    }

    private void OnTileFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile } button)
        {
            _focusedKey = tile.Game.Key;
            _focusInContinue = InContinueRow(button);
            AcceptHint.Text = AcceptText();
            _focusInDeals = false;
            QueueBackdrop(tile);

            if (!tile.IsInstalled)
            {
                LoadNotInstalledNear(_tiles.IndexOf(tile));
            }

            // The Old School cards all sit in one place and come to the focus, so only the page moves.
            if (IsOldSchool && !_focusInContinue)
            {
                CardFocused(GameCards, tile, _tiles.IndexOf(tile));
                LoadCardArt();
                TilesScroller.BringIntoView(new Rect(0, -20, TilesScroller.ActualWidth, TilesScroller.ActualHeight + 60));
                return;
            }

            // Leave room for the focus ring and the title underneath when scrolling a row into view.
            // In the row the focused tile is drawn well beyond where it is laid out.
            var (across, above) = IsRow ? (95d, 170d) : (20d, 40d);
            button.BringIntoView(new Rect(-across, -above, button.ActualWidth + (2 * across), button.ActualHeight + above + 40));
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
