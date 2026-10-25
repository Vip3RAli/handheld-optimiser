using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The parts of the window the controller, the keys and the pointer move about in.</summary>
internal sealed record InputParts(
    ItemsControl Tiles,
    ScrollViewer TilesScroller,
    ScrollViewer Scroller,
    TextBlock AcceptHint,
    Button ContinueCard,
    TopBarView TopBar,
    MenuView Menu);

/// <summary>
/// Where each press of the controller, each key, and each click, tap or wheel turn on the games goes. Which
/// of those has the input is <see cref="LibraryNavigation"/>'s answer; this moves the focus about the page
/// when the page has it: through the grid by row and column, to the top bar and the Last played card above,
/// to the deals below, and along the Old School cards.
/// </summary>
internal sealed class LibraryNavigationCoordinator
{
    private readonly TileGrid _grid;
    private readonly ItemsControl _tiles;
    private readonly ScrollViewer _tilesScroller;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _acceptHint;
    private readonly TopBarView _topBarView;
    private readonly MenuView _menuView;
    private readonly LibraryNavigation _navigation;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly LibraryLayoutManager _layouts;
    private readonly MenuPresenter _menu;
    private readonly SearchPresenter _search;
    private readonly TopBarPresenter _topBar;
    private readonly ContinuePresenter _continue;
    private readonly DealsPresenter _deals;
    private readonly OldSchoolPresenter _oldSchool;
    private readonly GameActionsMenu _gameActions;
    private readonly SettingsMenu _settings;
    private readonly PowerMenu _power;
    private readonly GameSessionController _play;
    private readonly TileArtwork _artwork;
    private readonly BackdropPresenter _backdrop;
    private readonly ILibraryHost _host;

    public LibraryNavigationCoordinator(TileGrid grid, InputParts parts, LibraryNavigation navigation, LibraryState state,
        LibraryOptions options, LibraryLayoutManager layouts, MenuPresenter menu, SearchPresenter search, TopBarPresenter topBar,
        ContinuePresenter continuePresenter, DealsPresenter deals, OldSchoolPresenter oldSchool, GameActionsMenu gameActions,
        SettingsMenu settings, PowerMenu power, GameSessionController play, TileArtwork artwork, BackdropPresenter backdrop,
        ILibraryHost host)
    {
        _grid = grid;
        _tiles = parts.Tiles;
        _tilesScroller = parts.TilesScroller;
        _scroller = parts.Scroller;
        _acceptHint = parts.AcceptHint;
        _topBarView = parts.TopBar;
        _menuView = parts.Menu;
        _navigation = navigation;
        _state = state;
        _options = options;
        _layouts = layouts;
        _menu = menu;
        _search = search;
        _topBar = topBar;
        _continue = continuePresenter;
        _deals = deals;
        _oldSchool = oldSchool;
        _gameActions = gameActions;
        _settings = settings;
        _power = power;
        _play = play;
        _artwork = artwork;
        _backdrop = backdrop;
        _host = host;

        // Every game's tile, the Last played card among them.
        Visuals.OnItemButtons(parts.Tiles, OnTileClick, OnTileFocused, OnTileRightClick);
        parts.ContinueCard.Click += OnTileClick;
        parts.ContinueCard.GotKeyboardFocus += OnTileFocused;
        parts.ContinueCard.MouseRightButtonUp += OnTileRightClick;
        parts.TilesScroller.PreviewMouseWheel += OnTilesWheel;
    }

    /// <summary>
    /// Moves through the grid by row and column. WPF's own directional navigation only moves to a tile
    /// directly in line, so Down did nothing from a column past the end of a shorter last row.
    /// </summary>
    public void Navigate(FocusNavigationDirection direction)
    {
        switch (_navigation.Input)
        {
            case InputTarget.Menu:
                _menu.MoveFocus(direction);
                return;

            case InputTarget.Page:
                break;

            // Nothing moves while the card grows or the text box is open, and the d-pad moves around
            // Windows' keyboard while the search box is being typed in.
            default:
                return;
        }

        // In the Old School layout up and down are between the categories, whatever has the focus, and
        // left and right along the cards of the one that is open.
        if (_layouts.IsOldSchool && direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down)
        {
            _oldSchool.StepCategory(direction == FocusNavigationDirection.Up ? 1 : -1);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: ActionCard })
        {
            _oldSchool.NavigateCards(_oldSchool.ActionStack, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button topBar && _topBar.IsButton(topBar))
        {
            _topBar.Navigate(topBar, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: DealTile } deal)
        {
            _deals.NavigateDeals(deal, direction);
            return;
        }

        if (Keyboard.FocusedElement is Button { DataContext: DealSourceTile } source)
        {
            _deals.NavigateDealSources(source, direction);
            return;
        }

        if (Keyboard.FocusedElement is not Button { DataContext: GameTile tile } button)
        {
            // With nothing to show, such as an empty Apps tab, the top bar is all there is to reach.
            if (_state.Tiles.Count == 0 && direction == FocusNavigationDirection.Up)
            {
                _topBar.Focus();
            }
            else
            {
                _host.RestoreFocus();
            }

            return;
        }

        if (_continue.Contains(button))
        {
            NavigateContinue(direction);
            return;
        }

        var index = _state.Tiles.IndexOf(tile);
        var columns = _grid.ColumnCount();
        var last = _state.Tiles.Count - 1;

        // Up from the grid's first row goes to the Last played card above it, or to the top bar.
        if (direction == FocusNavigationDirection.Up && index >= 0 && index < columns)
        {
            if (!_continue.Focus())
            {
                _topBar.Focus();
            }

            return;
        }

        // Down from the grid's last row goes to the Deals row below it.
        if (direction == FocusNavigationDirection.Down && index >= 0 && index / columns == last / columns && _deals.FocusDealBelow(button))
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
            _grid.Focus(target);
        }
    }

    /// <summary>Up from the Last played card to the top bar, and down into the grid.</summary>
    private void NavigateContinue(FocusNavigationDirection direction)
    {
        switch (direction)
        {
            case FocusNavigationDirection.Up:
                _topBar.Focus();
                break;
            case FocusNavigationDirection.Down when _state.Tiles.Count > 0:
                _grid.Focus(0);
                break;
        }
    }




    public void Press(GamepadAction action)
    {
        var target = _navigation.Input;

        // The Not installed card is on its way open, and what the press was meant for is on its way out.
        if (target == InputTarget.Nowhere)
        {
            return;
        }

        if (target == InputTarget.TextBox)
        {
            // The Windows touch keyboard takes A, B, X and Y for typing, so those must not act here too.
            switch (action)
            {
                case GamepadAction.Menu:
                    _menu.SaveEdit();
                    break;
                case GamepadAction.View:
                    _menu.ShowItems();
                    break;
            }

            return;
        }

        if (target == InputTarget.SearchBox)
        {
            // As in the text editor, the letters are typed with A, X and Y on Windows' keyboard.
            switch (action)
            {
                case GamepadAction.Menu:
                    _search.Finish();
                    break;
                case GamepadAction.View:
                    _search.ClearTyping();
                    break;

                // B puts Windows' keyboard away, and with it the cursor: the bar is picked out again with
                // what was typed kept, and B once more closes the search.
                case GamepadAction.Back:
                    _search.EndTyping();
                    break;
            }

            return;
        }

        if (target == InputTarget.Menu)
        {
            switch (action)
            {
                case GamepadAction.Accept when Keyboard.FocusedElement is Button item && _menu.Contains(item):
                    item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case GamepadAction.Back:
                    _menu.Back();
                    break;

                // Y opens quick settings and closes them again, like Armoury Crate's Command Center button.
                case GamepadAction.QuickSettings when ReferenceEquals(_menu.List, _menuView.QuickItems):
                    _menu.Close();
                    break;
            }

            return;
        }

        switch (action)
        {
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                _play.Launch(tile);
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: DealTile deal }:
                _deals.OpenDeal(deal);
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: DealSourceTile } source:
                source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: ActionCard card }:
                card.Act();
                break;

            // B in the Old School layout's open Not installed card closes it.
            case GamepadAction.Back when !_search.IsOpen && _oldSchool.LeaveNotInstalled():
                break;
            case GamepadAction.Accept when Keyboard.FocusedElement is Button topBar && _topBar.IsButton(topBar):
                // The search button opens differently for the controller than for a tap.
                _search.PressedByController = true;
                try
                {
                    topBar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally
                {
                    _search.PressedByController = false;
                }

                break;
            case GamepadAction.Options when _options.QuickActions && Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                _gameActions.Open(tile);
                break;

            // B among the games a search found closes it and shows them all again.
            case GamepadAction.Back when _search.IsOpen:
                _search.Clear();
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

    public void KeyDown(object sender, KeyEventArgs e)
    {
        var target = _navigation.Input;
        if (target == InputTarget.Nowhere)
        {
            e.Handled = true;
            return;
        }

        if (target == InputTarget.TextBox)
        {
            // Every other key belongs to the text box.
            if (e.Key == Key.Enter)
            {
                _menu.SaveEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _menu.ShowItems();
                e.Handled = true;
            }

            return;
        }

        if (target == InputTarget.SearchBox)
        {
            // Every other key belongs to the search box.
            if (e.Key is Key.Enter or Key.Down)
            {
                _search.Finish();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _search.EndTyping();
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
            Navigate(d);
            e.Handled = true;
        }
        else if (target == InputTarget.Menu)
        {
            if (e.Key == Key.Escape)
            {
                _menu.Back();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _search.IsOpen)
        {
            _search.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _oldSchool.LeaveNotInstalled())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _host.ScanAsync();
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
        else if (_options.QuickActions && e.Key is Key.X or Key.Apps && Keyboard.FocusedElement is Button { DataContext: GameTile tile })
        {
            _gameActions.Open(tile);
            e.Handled = true;
        }
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile })
        {
            // A tap on an Old School card behind the front one only brings it forward, which taking the
            // focus has just done. The next tap, on what is now the front card, starts its game.
            if (_oldSchool.CardClickSwallowed(_oldSchool.GameCards, tile))
            {
                return;
            }

            _play.Launch(tile);
        }
    }

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_options.QuickActions && sender is Button { DataContext: GameTile tile } button)
        {
            // An Old School card turns over to show its quick actions, which it does at the front. One
            // behind the front comes forward first, and the next press and hold turns it.
            if (_layouts.IsOldSchool && _state.Tiles.IndexOf(tile) != _oldSchool.GameCards.Front)
            {
                button.Focus();
            }
            else
            {
                _gameActions.Open(tile);
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
        if (_layouts.Layout == LibraryLayout.OldSchool)
        {
            _oldSchool.LeaveNotInstalled();
            _oldSchool.OpenCategoryOf(CategoryKind.Settings);
        }
        else
        {
            _settings.Open();
        }
    }

    /// <summary>The View button: the power menu, which in the Old School layout is the cards of its own category.</summary>
    private void OpenPowerOrItsCards()
    {
        if (_layouts.Layout == LibraryLayout.OldSchool)
        {
            _oldSchool.LeaveNotInstalled();
            _oldSchool.OpenCategoryOf(CategoryKind.Power);
        }
        else
        {
            _power.Open();
        }
    }

    private void OnTileFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile } button)
        {
            _state.FocusedKey = tile.Game.Key;
            _continue.FocusWasHere = _continue.Contains(button);
            _acceptHint.Text = _state.Filter.Accept;
            _deals.FocusWasHere = false;
            _backdrop.Queue(tile);

            if (!tile.IsInstalled)
            {
                _artwork.LoadNotInstalledNear(_state.Tiles.IndexOf(tile));
            }

            // The Old School cards all sit in one place and come to the focus, so only the page moves.
            if (_layouts.IsOldSchool && !_continue.FocusWasHere)
            {
                _oldSchool.CardFocused(_oldSchool.GameCards, tile, _state.Tiles.IndexOf(tile));
                _artwork.LoadCardArt();
                _tilesScroller.BringIntoView(new Rect(0, -20, _tilesScroller.ActualWidth, _tilesScroller.ActualHeight + 60));
                return;
            }

            // Leave room for the focus ring and the title underneath when scrolling a row into view.
            // In the row the focused tile is drawn well beyond where it is laid out.
            var (across, above) = _layouts.IsRow ? (95d, 170d) : (20d, 40d);
            button.BringIntoView(new Rect(-across, -above, button.ActualWidth + (2 * across), button.ActualHeight + above + 40));
        }
    }

    // How far the wheel has turned over the row since it last moved on a game. A mouse sends a whole
    // notch at a time, and a touchpad many small parts of one.
    private int _rowWheel;

    /// <summary>
    /// The mouse wheel over the games. In the row each notch moves on to the next game, as the d-pad
    /// does, which brings it into view. The grid has nothing of its own to scroll, so the wheel is passed
    /// on to the page, which a scroll viewer does not do by itself.
    /// </summary>
    private void OnTilesWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_layouts.IsOneLine)
        {
            _scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
            return;
        }

        _rowWheel += e.Delta;
        var steps = _rowWheel / Mouse.MouseWheelDeltaForOneLine;
        if (steps == 0 || _navigation.MenuOpen || _state.Tiles.Count == 0)
        {
            return;
        }

        // Down or towards the player is on to the right.
        _rowWheel -= steps * Mouse.MouseWheelDeltaForOneLine;
        var from = Math.Max(0, _state.FocusedIndex);
        _grid.Focus(Math.Clamp(from - steps, 0, _state.Tiles.Count - 1));
    }

    /// <summary>
    /// After the grid scrolled: loads the pictures of the games now on screen. Moving the focus scrolls
    /// too, but so do touch and the mouse wheel, which leave the focus where it was.
    /// </summary>
    public void OnGridScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!_state.Filter.NotInstalled || _layouts.IsRow || e.VerticalChange == 0 || _state.Tiles.Count == 0
            || _grid.Container(0) is not { ActualHeight: > 0 } first)
        {
            return;
        }

        // How many rows have gone off the top, and so the first game still on screen.
        var above = -_tiles.TranslatePoint(default, _scroller).Y;
        var row = Math.Max(0, (int)(above / first.ActualHeight));
        var columns = _grid.ColumnCount();
        var index = Math.Min(row * columns, _state.Tiles.Count - 1);

        // Left alone while the game they were loaded around is still among the first rows on screen, as
        // the focused game is when it was the d-pad that scrolled.
        var near = _state.Tiles.FindIndex(t => t.Game.Key == _artwork.PicturesNearKey);
        if (near < index || near >= index + 3 * columns)
        {
            _artwork.LoadNotInstalledNear(index);
        }
    }

    /// <summary>
    /// LB and RB: one place left or right along the whole top bar, wrapping around. A tab is opened as it
    /// is reached, with the focus back among its games. The buttons after the tabs (search on the Not
    /// installed tab, settings and power) are picked out for A to press.
    /// </summary>
    private void CycleFilter(int step)
    {
        // The Old School layout has its categories in place of the top bar, and the bumpers move along
        // those. From the open Not installed card that closes it first.
        if (_layouts.Layout == LibraryLayout.OldSchool)
        {
            _oldSchool.LeaveNotInstalled();
            _oldSchool.StepCategory(step > 0 ? 1 : -1);
            return;
        }

        var tabs = TileFilter.Options(_state.Shown(_options.ShowHidden), _options.ShowFilter);
        var buttons = new List<Button>();
        if (_topBarView.SearchButton.Visibility == Visibility.Visible)
        {
            buttons.Add(_topBarView.SearchButton);
        }

        buttons.Add(_topBarView.SettingsButton);
        buttons.Add(_topBarView.PowerButton);

        // From a button, or from the open tab. The search bar and its store button count as the search button.
        var focused = Keyboard.FocusedElement as Button;
        var button = ReferenceEquals(focused, _topBarView.SearchBar) || ReferenceEquals(focused, _topBarView.StoreButton) ? _topBarView.SearchButton : focused;
        var at = button is not null && buttons.IndexOf(button) is >= 0 and var index ? tabs.Count + index : tabs.IndexOf(_state.Filter);

        var count = tabs.Count + buttons.Count;
        var next = (((at + step) % count) + count) % count;
        if (next >= tabs.Count)
        {
            buttons[next - tabs.Count].Focus();
            return;
        }

        _host.ShowTab(tabs[next]);
        _host.RestoreFocus(leavingTopBar: true);
    }
}
