using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The full screen game library. It stays open behind whatever game it starts, so the home button and
/// quitting a game both land back here, but it gives its memory back and ignores the controller while it
/// is not in front.
/// </summary>
public partial class LibraryWindow : Window, ILibraryHost
{
    // A new scan on return picks up games installed meanwhile, without rescanning on every alt-tab.
    private static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(2);

    private readonly GamepadInput _gamepad;
    private readonly DispatcherTimer _clock;

    // Where the player is and what has the input: the page, the open Not installed card, the menu, its
    // text box or the search box.
    private readonly LibraryNavigation _navigation = new(Program.Log);

    // The games, the tab and the tiles on it, the focused game and the play times, which every part reads.
    private readonly LibraryState _state = new();

    // The player's display choices, read again each time the library comes to the front.
    private readonly LibraryOptions _options = new();

    // Which layout the library is drawn in and at what size, on the built-in screen or a TV.
    private readonly LibraryLayoutManager _layouts;

    // The parts of the library that run on their own, given what they need as they are made.
    private readonly NoticePresenter _notices;
    private readonly BackdropPresenter _backdrop;
    private readonly MenuPresenter _menu;
    private readonly TopBarPresenter _topBar;
    private readonly SearchPresenter _search;
    private readonly TileArtwork _artwork;
    private readonly ContinuePresenter _continue;

    // The menu's lists, each run by the menu it belongs to.
    private readonly PowerMenu _power;
    private readonly OwnedGamesMenu _owned;
    private readonly GameActionsMenu _gameActions;
    private readonly SettingsMenu _settings;

    // The game being played, from the press that starts it to the moment it closes.
    private readonly GameSessionController _play;

    // The Old School layout: its categories, its stacks of cards and the open Not installed card.
    private readonly OldSchoolPresenter _oldSchool;

    // The Deals row, and its settings.
    private readonly DealsPresenter _deals;

    // The games' tiles as they are laid out, and where each press goes.
    private readonly TileGrid _grid;
    private readonly LibraryNavigationCoordinator _input;

    private bool _scanning;

    public LibraryWindow()
    {
        InitializeComponent();

        _layouts = new LibraryLayoutManager(this, _state);
        _layouts.ScreenChanged += ApplyLayout;
        _grid = new TileGrid(Tiles, _state, _layouts);

        _notices = new NoticePresenter(Menu.MenuNotice, _navigation, NoticeHost);
        _backdrop = new BackdropPresenter(BackdropFront, BackdropBack, _state, _options, _navigation, () => _layouts.IsBrowsing);
        _menu = new MenuPresenter(Menu, _navigation, _notices, CardToTurn, () => _layouts.IsBrowsing, ScaledContent, () => RestoreFocus());
        _search = new SearchPresenter(TopBar, _navigation, _state, _options, () => new WindowInteropHelper(this).Handle,
            ShowFound, ApplyFilter, leaving => RestoreFocus(leaving), IsTopBarButton);
        _topBar = new TopBarPresenter(TopBar, UpdateBanner, UpdateText, UpdateNowButton, DismissUpdateButton, AcceptHint,
            _options, _notices, () => _layouts.IsBrowsing, () => _search.IsOpen, _menu.Close, leaving => RestoreFocus(leaving));
        _artwork = new TileArtwork(_state, _navigation, _backdrop, () => _layouts.IsRow, () => _layouts.IsOldSchool, CardPixels);
        _continue = new ContinuePresenter(Continue, Scroller, _state, _options, _navigation, () => _layouts.IsRow, () => _layouts.IsOldSchool,
            () => _layouts.Docked, IsPausedGame, () => _grid.Container(0));

        _play = new GameSessionController(this, _state, _navigation, _notices, _continue, this, ScreenOff, ScreenOn, ShowLibrary);
        _power = new PowerMenu(Menu, _menu, _notices, this);
        _owned = new OwnedGamesMenu(Menu, _menu, _notices, _state, this);
        _gameActions = new GameActionsMenu(Menu, _menu, _notices, _state, _options, _artwork, _backdrop, _continue, this);
        _settings = new SettingsMenu(Menu, _menu, _notices, _state, _options, _backdrop, _artwork, _topBar, _continue, OptionsHint, this);
        _ = new AddProgramMenu(Menu, _menu, _notices, _state, this);
        _ = new EmulatorsMenu(Menu, _menu, _notices, _state, this);
        _ = new SleepAndCleanUpMenu(Menu, _menu, _notices, this);

        // Nothing opens quick settings now, as its button and the controller's Y were taken away. Its menu is
        // kept, wired, for when they come back.
        _ = new QuickSettingsMenu(Menu, _menu, _notices);
        _oldSchool = new OldSchoolPresenter(this,
            new OldSchoolParts(Categories, Tiles, DealRow.DealTiles, ActionCards, Scroller, TilesScroller, Page, OpenCard, OpenCardBack,
                OpenCardHeading, OpenCardCount, OpenCardSearch, CloseOpenCardButton, BackHint, ScaledContent, AcceptHint, OptionsHint,
                TopBar, Menu),
            _state, _options, _navigation, _layouts, _notices, _backdrop, _artwork, _topBar, _settings, _power, this,
            DealsShown, FocusedDealId, UpdateDeals, UpdateEmptyText);
        _deals = new DealsPresenter(DealRow, this, Scroller, AcceptHint, Menu, _menu, _notices, _state, _navigation, _layouts,
            _oldSchool, _backdrop, _play, _grid.ColumnCount, _grid.Container, _grid.Focus);
        _input = new LibraryNavigationCoordinator(_grid,
            new InputParts(Tiles, TilesScroller, Scroller, AcceptHint, Continue.ContinueCard, TopBar, Menu),
            _navigation, _state, _options, _layouts, _menu, _search, _topBar, _continue, _deals, _oldSchool, _gameActions, _settings,
            _power, _play, _artwork, _backdrop, this);
        _topBar.SettingsPressed += _settings.Open;
        _topBar.PowerPressed += _power.Open;

        _gamepad = new GamepadInput(Dispatcher);
        _gamepad.Navigate += _input.Navigate;
        _gamepad.Pressed += _input.Press;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => _topBar.Refresh(), Dispatcher);

        // The battery and Wi-Fi wait for Loaded. The window is made before WPF's dispatcher runs, so a
        // reading started here finished on the thread pool and could not touch the top bar.
        TopBar.ClockText.Text = DateTime.Now.ToString("t");
        Loaded += (_, _) => _topBar.Refresh();

        // Closing the library also ends what was changed for a game: its profile and closed programs.
        Closed += (_, _) => EndSession();
        Closed += (_, _) => _layouts.StopWatching();
        Closed += (_, _) => _play.StopWatchingWake();
        Closed += (_, _) => _play.StopWatchingForeground();

        // Scan on load, not only on activation: Windows can start the home app without giving it focus.
        Loaded += (_, _) => _ = ScanAsync();
        Loaded += (_, _) => _ = _topBar.CheckForUpdateAsync();
        Loaded += (_, _) => _ = RefreshGameSettingsAsync();
        Loaded += (_, _) => WatchScreens();
        SourceInitialized += (_, _) => _play.WatchWake();
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        PreviewKeyDown += _input.KeyDown;
    }

    /// <summary>Brings the library to the front; called when the home button starts a second instance.</summary>
    public void ShowLibrary()
    {
        _play.ForgetWake();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Maximized;
        }

        Show();
        Activate();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _navigation.Return();

        // About fifteen settings are read below and in what it calls; the registry key is opened once for them.
        using var settings = LibrarySettings.ReadOnce();

        // Just woken with a game running: straight back to it. Otherwise the game is paused, with Quick Resume.
        _play.Returned();

        // Not while the screen is off: Windows can bring the library to the front as the device sleeps.
        if (!_play.DisplayOff)
        {
            _gamepad.Start();
        }

        _options.Reload();
        OptionsHint.Visibility = _options.QuickActions ? Visibility.Visible : Visibility.Collapsed;

        // Changed in another session, or a screen plugged in while a game was in front.
        var layoutChanged = _layouts.LayoutChanged();
        var (continueChanged, infoChanged) = _options.ReloadContinue();
        if (continueChanged || layoutChanged)
        {
            if (infoChanged)
            {
                _continue.ForgetInfo();
            }

            ApplyLayout();
        }

        _continue.ShowArt();
        _artwork.LoadSquares();
        _oldSchool.UpdateRenderMode();
        _artwork.LoadCardArt();

        // The deals kept on disk at once, and new ones if they are old and no game is running.
        UpdateDeals();
        _ = _deals.RefreshDealsAsync(force: false);

        // Switched in the main app or another session: the strip follows on the next look at the tiles.
        if (_options.ReloadShowFilter())
        {
            ApplyFilter();
        }
        if (!_play.DisplayOff)
        {
            _clock.Start();
        }

        _topBar.Refresh();
        _ = _topBar.CheckForUpdateAsync();

        if (DateTime.UtcNow - _state.LastScan > RescanAfter)
        {
            _ = ScanAsync();
        }
        else
        {
            RestoreFocus();
        }

        // Back from a game the library cannot follow, or in front of one TDP Engine is holding.
        _play.CheckWhileAway();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _navigation.Leave();
        _gamepad.Stop();
        _clock.Stop();
        Notice = string.Empty;
        _menu.Close();

        // The open Not installed card holds a tile for every game the player owns and has not installed,
        // hundreds of them, which is about 90 MB that nothing needs behind a game. It goes back to the
        // card it grew from, and A opens it again on the game it was left on.
        if (_layouts.IsBrowsing)
        {
            // The tile with the keyboard focus is about to be taken out of the window. Left holding
            // it, WPF would hand the focus to the window itself as the tile went, which makes the
            // library the active window again, in front of the game or store that just opened.
            Keyboard.ClearFocus();
            _oldSchool.LeaveNotInstalled();
        }

        _backdrop.Clear();
        _continue.ClearArt();
        _artwork.UnloadSquares();
        _artwork.UnloadCardArt();
        _artwork.UnloadNotInstalled();
        _deals.UnloadDeals();
        foreach (var tile in _state.AllTiles)
        {
            tile.UnloadBrushes();
        }
        _oldSchool.UpdateRenderMode();

        // Once the rendering of the focus change has gone out, nothing here is needed until we are back.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Native.TrimWorkingSet);
    }

    /// <param name="refreshEmulators">Look for emulators again too, which takes a moment.</param>
    private async Task ScanAsync(bool refreshEmulators = false)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        if (_state.AllTiles.Count == 0)
        {
            EmptyText.Text = "Looking for your games...";
        }

        try
        {
            var previous = _state.AllTiles;
            var (tiles, stats) = await Task.Run(() =>
            {
                var games = GameCatalog.Scan(refreshEmulators);
                GameFolders.Save(games);
                return (GameTile.ForScan(games, previous), Playtime.Read(GameCatalog.LastLaunched()));
            });
            _state.LastScan = DateTime.UtcNow;

            // Rebuilding the tiles resets scrolling, so only do it when something changed. New tiles take
            // their stars and hidden marks here; kept ones already have them.
            var marksChanged = MarkTiles(tiles);
            var statsChanged = !LibraryState.SameStats(stats, _state.Stats);
            _state.Stats = stats;
            if (marksChanged || statsChanged || !tiles.Select(t => t.Game).SequenceEqual(_state.AllTiles.Select(t => t.Game)))
            {
                _state.AllTiles = tiles;
                ApplyFilter();
            }

            UpdateEmptyText();
        }
        catch (Exception ex) when (!LibraryApp.IsFatal(ex))
        {
            // Nobody awaits a scan, so a failure would only reach the log as unobserved. The grid keeps the
            // games it had, and the next activation scans again.
            Program.Log($"The game scan failed: {ex}");
            UpdateEmptyText();
        }
        finally
        {
            _scanning = false;
        }

        RestoreFocus();
        _ = _artwork.FetchCoversAsync();
        _ = _owned.RefreshAsync(force: false);
    }

    /// <summary>The games the grid can show: all but the hidden ones, unless those are shown too.</summary>
    private IEnumerable<GameTile> ShownTiles() => _state.Shown(_options.ShowHidden);

    /// <summary>The tabs of the filter strip, in order. See <see cref="TileFilter.Options"/>.</summary>
    private List<TileFilter> FilterOptions() => TileFilter.Options(ShownTiles(), _options.ShowFilter);

    /// <summary>Shows the tiles of the current filter and rebuilds the filter strip to match the library.</summary>
    private void ApplyFilter()
    {
        var options = FilterOptions();

        // The filtered store's last game may just have been uninstalled, or the last favourite unstarred.
        if (!options.Contains(_state.Filter))
        {
            _state.Filter = TileFilter.All;
        }

        TopBar.FilterTabs.Children.Clear();
        foreach (var option in options)
        {
            TopBar.FilterTabs.Children.Add(FilterTab(option));
        }

        // Search is only on the Not installed tab, and is forgotten when that is left.
        TopBar.SearchButton.Visibility = _state.Filter.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
        if (!_state.Filter.NotInstalled)
        {
            _search.Reset();
        }

        // The Not installed tab is a grid whatever the layout, so a change of tab may change how they are laid out.
        ApplyTilePanel();
        _oldSchool.RefreshCategories();

        // Favourites first, each group in the chosen order.
        _state.Tiles = _state.Sorted(ShownTiles().Where(_state.Filter.Shows).Where(_search.Found), _options.Sort)
            .OrderByDescending(t => t.IsFavourite).ToList();
        Tiles.ItemsSource = _state.Tiles;
        AcceptHint.Text = _state.Filter.Accept;
        _oldSchool.UpdateOpenCardCount();

        // The pictures of games that are not installed are only kept while their tab is open.
        if (!_state.Filter.NotInstalled)
        {
            _artwork.UnloadNotInstalled();
        }

        _continue.Update();
        UpdateDeals();
        UpdateEmptyText();
        _artwork.LoadSquares();
        _oldSchool.QueueCardReset();
    }

    private void UpdateEmptyText()
    {
        // Until the first scan is in, the text says the games are being looked for.
        if (!_state.Scanned)
        {
            return;
        }

        // An Old School category with cards of its own is never empty.
        if (_oldSchool.OpenKind is { } kind && kind != CategoryKind.Games)
        {
            EmptyText.Text = string.Empty;
            return;
        }

        EmptyText.Text = _search.Text.Length > 0
            ? _state.Tiles.Count == 0
                ? $"No games that are not installed match \"{_search.Text}\"{(_search.Store is { } store ? $" on {Game.NameOf(store)}" : string.Empty)}."
                : string.Empty
            : _state.Filter.Apps
            ? _state.Tiles.Count == 0
                ? "No apps yet. Press Menu, then Catalog & Content, then Add Custom App."
                : string.Empty
            : _state.AllTiles.All(t => !t.IsInstalled || t.IsApp) && !_state.Filter.NotInstalled
            ? "No installed games were found in Steam, Xbox, Epic Games, Battle.net, GOG, the EA App or Ubisoft Connect.\nInstall a game, or press Menu, then Catalog & Content, then Add Custom App."
            : _state.Tiles.Count == 0 ? "Every game is hidden. Turn on Show hidden games to bring them back. Press Menu, then Appearance & Layout, then Library Elements."
            : string.Empty;
    }

    private RadioButton FilterTab(TileFilter filter)
    {
        var tab = new RadioButton
        {
            Style = (Style)FindResource("FilterTab"),
            GroupName = "StoreFilter",
            Content = TabIcon(filter),
            ToolTip = filter.Name,
            IsChecked = filter == _state.Filter
        };

        tab.Click += (_, _) => SetFilter(filter);
        return tab;
    }

    /// <summary>
    /// What a tab's round button shows: a store's logo for its tab, and a symbol from Windows' icon font
    /// for the others.
    /// </summary>
    private static object TabIcon(TileFilter filter)
    {
        var logo = filter is { Store: { } store, Apps: false, NotInstalled: false }
            ? store == GameStore.Emulator ? StoreLogos.ForConsole("Arcade") : StoreLogos.For(store)
            : null;

        if (logo is null)
        {
            // Apps, a download, a star, an added program, and a controller for the games.
            return filter.Apps ? ""
                : filter.NotInstalled ? ""
                : filter.Favourites ? ""
                : filter.Store is not null ? ""
                : "";
        }

        // Filled in the colour of the button's own symbol, which is dark on the tab that is open.
        var shape = new System.Windows.Shapes.Path { Data = logo, Stretch = System.Windows.Media.Stretch.Uniform, Width = 20, Height = 20 };
        shape.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty),
            RelativeSource = System.Windows.Data.RelativeSource.Self
        });
        return shape;
    }

    private void SetFilter(TileFilter filter)
    {
        if (filter == _state.Filter)
        {
            return;
        }

        _state.Filter = filter;

        // Another tab's Old School cards slide in, as they do when the layout is first shown.
        _oldSchool.GameCards.Fresh = true;
        ApplyFilter();

        // From the Not installed grid's own tabs to a tab of games: that tab's category is the open one.
        _oldSchool.OpenCategoryOfFilter();
        _oldSchool.ShowCategoryCards();
        UpdateDeals();
        RestoreFocus();
    }

    /// <summary>
    /// Puts the focus back on the game it was on. A rescan or a new cover must not pull the d-pad off the
    /// top bar or out of the search box, so the focus stays there unless it is the top bar asking to leave.
    /// </summary>
    private void RestoreFocus(bool leavingTopBar = false)
    {
        if (!MayRestoreFocus(leavingTopBar))
        {
            return;
        }

        // An Old School category with cards of its own, not games: the card at the front of those.
        if (_oldSchool.OpenKind is { } kind && kind != CategoryKind.Games)
        {
            _oldSchool.FocusFrontCard();
            return;
        }

        if (_state.Tiles.Count == 0)
        {
            return;
        }

        // Wait for the item containers to exist after ItemsSource changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Asked again: a menu may have opened, or the d-pad gone to the top bar, while this waited.
            if (!MayRestoreFocus(leavingTopBar))
            {
                return;
            }

            // Back on the deal it was on, when that deal is still listed.
            if (_deals.RestoreDealFocus())
            {
                return;
            }

            // Back on the Last played card it was on, when that game is still there.
            if (_continue.RestoreFocus() is { } card)
            {
                _backdrop.Queue(card);
                return;
            }

            var index = Math.Max(0, _state.FocusedIndex);
            _grid.Focus(index);

            // A tile that kept the focus raises no focus event, and its background (or its pictures, for a
            // game that is not installed) may have been released.
            if (index < _state.Tiles.Count)
            {
                _backdrop.Queue(_state.Tiles[index]);
                _artwork.LoadNotInstalledNear(index);
            }
        });
    }

    /// <summary>Shows a notice for a few seconds, wherever the player is looking. Empty takes the last one away.</summary>
    private string Notice
    {
        set => _notices.Show(value);
    }

    /// <summary>
    /// The tile or card the player is looking at, for a notice that names none: the front card in the Old
    /// School layout, and in the others whatever has the focus, or the game it was last on.
    /// </summary>
    private INoticeHost? NoticeHost()
    {
        if (_oldSchool.OpenStack is { } stack && stack.Front >= 0 && stack.Front < stack.Items.Items.Count
            && stack.Items.Items[stack.Front] is INoticeHost card)
        {
            return card;
        }

        return Keyboard.FocusedElement is FrameworkElement { DataContext: INoticeHost focused } ? focused
            : _state.FocusedTile;
    }

    /// <summary>Shows the games a search now finds, from the top.</summary>
    private void ShowFound()
    {
        ApplyFilter();

        // The pictures are loaded around the focus, which is in the top bar: the first games found get theirs.
        Scroller.ScrollToTop();
        if (_state.Tiles.Count > 0)
        {
            _state.FocusedKey = _state.Tiles[0].Game.Key;
            _artwork.LoadNotInstalledNear(0);
        }
    }

    /// <summary>A game's Old School card's size in real pixels on this screen, which its artwork is made at.</summary>
    private (int Width, int Height) CardPixels() => _oldSchool.CardPixels();

    /// <summary>The deals on show, for the Old School categories, or null when there are none or the row is off.</summary>
    private List<DealTile>? DealsShown() => _deals.Shown;

    /// <summary>The deal the focus was last on.</summary>
    private string? FocusedDealId() => _deals.FocusedDealId;

    /// <summary>Shows the deals for the category or tab that is open.</summary>
    private void UpdateDeals() => _deals.UpdateDeals();

    /// <summary>Whether an element is one of the top bar's buttons the d-pad goes along.</summary>
    private bool IsTopBarButton(object? element) => _topBar.IsButton(element);

    /// <summary>
    /// In the Old School layout, the open category's panel and the card at its front, which turns over to
    /// show the menu. Null in the other layouts.
    /// </summary>
    private (OldSchoolPanel Panel, Button Card)? CardToTurn() =>
        _layouts.IsOldSchool && _oldSchool.OpenStack is { Panel: { } panel } stack
            && ItemContainers.At(stack.Items, stack.Front) is { } container && Visuals.Child<Button>(container) is { } card
            ? (panel, card)
            : null;

    /// <summary>
    /// Whether the focus may go back to the page now: the library is in front with nothing over the page,
    /// and the d-pad is not on the top bar, unless it is the top bar or the search box asking to leave.
    /// </summary>
    private bool MayRestoreFocus(bool leavingTopBar) =>
        _navigation.MayRestoreFocus(leavingTopBar) && (leavingTopBar || !_topBar.IsButton(Keyboard.FocusedElement));

    /// <summary>
    /// The Armoury Crate button, passed on by a second start of the launcher. Pressed while the library is
    /// in front it sends the player back, as Armoury Crate did; otherwise it brings the library forward.
    /// </summary>
    public void OnArmouryCrateButton() => _play.OnArmouryCrateButton();

    /// <summary>
    /// Takes the foreground for this window. Started by TDP Engine's service, the library does not own it,
    /// and Windows only lets it be taken right after input, so this runs as the window is first drawn.
    /// </summary>
    public void BringToFront() => _play.BringToFront();

    /// <summary>
    /// Puts everything back, for the library closing in the middle of a game. Runs once: from the window
    /// closing, and again from LibraryApp.Run's finally in case the library failed before it could close.
    /// </summary>
    internal void EndSession() => _play.EndSession();

    /// <summary>Whether this is the game paused right now.</summary>
    private bool IsPausedGame(GameTile tile) => _play.IsPaused(tile);

    /// <summary>
    /// Nobody can see the library with the screen off, and on a Modern Standby device it stays running
    /// then: the controller is polled every 16 ms while it is in front, which kept the processor from
    /// resting the whole time the device was asleep. Polling and the clock stop until the screen is back.
    /// </summary>
    private void ScreenOff()
    {
        _gamepad.Stop();
        _clock.Stop();
    }

    /// <summary>Starts again what <see cref="ScreenOff"/> stopped.</summary>
    private void ScreenOn()
    {
        _gamepad.Start();
        _clock.Start();
        _topBar.Refresh();
    }

    /// <summary>Puts the stored stars and hidden marks on the tiles.</summary>
    /// <returns>Whether any tile changed.</returns>
    private static bool MarkTiles(IEnumerable<GameTile> tiles)
    {
        var favourites = GameCatalog.Favourites();
        var hidden = GameCatalog.Hidden();
        var changed = false;

        foreach (var tile in tiles)
        {
            var favourite = favourites.Contains(tile.Game.Key);
            var isHidden = hidden.Contains(tile.Game.Key);
            changed |= tile.IsFavourite != favourite || tile.IsHidden != isHidden;
            tile.IsFavourite = favourite;
            tile.IsHidden = isHidden;
        }

        return changed;
    }

    /// <summary>Fetches a newer list in the background. The menu reads whichever list is newest when it opens.</summary>
    private async Task RefreshGameSettingsAsync()
    {
        if (await GameSettingsDb.RefreshAsync())
        {
            Program.Log("Fetched a newer list of recommended game settings");
        }
    }

    // ----- What the parts of the library ask of the window -----

    Window ILibraryHost.Window => this;

    LibraryLayout ILibraryHost.Layout => _layouts.Layout;

    bool ILibraryHost.Docked => _layouts.Docked;

    GameSession? ILibraryHost.Session => _play.Session;

    void ILibraryHost.ShowTiles() => ApplyFilter();

    void ILibraryHost.ShowTab(TileFilter tab) => SetFilter(tab);

    void ILibraryHost.RestoreFocus(bool leavingTopBar) => RestoreFocus(leavingTopBar);

    Task ILibraryHost.ScanAsync(bool refreshEmulators) => ScanAsync(refreshEmulators);

    void ILibraryHost.Launch(GameTile tile) => _play.Launch(tile);

    void ILibraryHost.LayoutSettingsChanged()
    {
        if (_layouts.LayoutChanged())
        {
            ApplyLayout();
        }
    }

    void ILibraryHost.ResumeGame(GameSession session) => _play.ResumeGame(session);

    Task ILibraryHost.CloseGameAsync(GameSession session) => _play.CloseGameAsync(session);

    bool ILibraryHost.IsPaused(GameTile tile) => _play.IsPaused(tile);
}
