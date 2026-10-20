using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The full screen game library. It stays open behind whatever game it starts, so the home button and
/// quitting a game both land back here, but it gives its memory back and ignores the controller while it
/// is not in front.
/// </summary>
public partial class LibraryWindow : Window
{
    // A new scan on return picks up games installed meanwhile, without rescanning on every alt-tab.
    private static readonly TimeSpan RescanAfter = TimeSpan.FromMinutes(2);

    // Guards against a second A press starting the game twice while its store client wakes up.
    private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(5);

    private readonly GamepadInput _gamepad;
    private readonly DispatcherTimer _clock;
    private List<GameTile> _allTiles = [];

    // Each game's play time and last play, for sorting and the quick actions menu.
    private Dictionary<string, PlayStats> _stats = [];

    // The tiles on screen: all the games, the favourites, one store's when a filter is on, or the apps.
    private List<GameTile> _tiles = [];
    private TileFilter _filter = TileFilter.All;

    private string? _focusedKey;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _launchBlockedUntil = DateTime.MinValue;
    private bool _scanning;

    public LibraryWindow()
    {
        InitializeComponent();

        _gamepad = new GamepadInput(Dispatcher);
        _gamepad.Navigate += OnNavigate;
        _gamepad.Pressed += OnGamepadPressed;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => UpdateTopBar(), Dispatcher);
        UpdateTopBar();

        // Closing the library also ends what was changed for a game: its profile and closed programs.
        Closed += (_, _) => EndSession();
        Closed += (_, _) => StopWatchingScreens();
        Closed += (_, _) => StopWatchingWake();
        Closed += (_, _) => StopWatchingForeground();

        // Scan on load, not only on activation: Windows can start the home app without giving it focus.
        Loaded += (_, _) => _ = ScanAsync();
        Loaded += (_, _) => _ = CheckForUpdateAsync();
        Loaded += (_, _) => _ = RefreshGameSettingsAsync();
        Loaded += (_, _) => WatchScreens();
        SourceInitialized += (_, _) => WatchWake();
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnKeyDown;
        WatchCardSwipes();
    }

    /// <summary>Brings the library to the front; called when the home button starts a second instance.</summary>
    public void ShowLibrary()
    {
        // The player asked for the library, so waking up does not send them back to their game.
        _wokeAt = null;

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Maximized;
        }

        Show();
        Activate();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // About fifteen settings are read below and in what it calls; the registry key is opened once for them.
        using var settings = LibrarySettings.ReadOnce();

        // Just woken with a game running: straight back to it. Otherwise the game is paused, with Quick Resume.
        _ = ResumeGameAsync();
        if (!_resuming && !JustWoke)
        {
            PauseOnReturn();
        }

        // Not while the screen is off: Windows can bring the library to the front as the device sleeps.
        if (!_displayOff)
        {
            _gamepad.Start();
        }

        _background = LibrarySettings.Background;
        _strength = LibrarySettings.Strength;
        _blur = LibrarySettings.Blur;
        _position = LibrarySettings.Position;
        _showStatus = LibrarySettings.ShowStatus;
        _quickActions = LibrarySettings.QuickActions;
        _showHidden = LibrarySettings.ShowHidden;
        _sort = LibrarySettings.Sort;
        OptionsHint.Visibility = _quickActions ? Visibility.Visible : Visibility.Collapsed;

        // Changed in another session, or a screen plugged in while a game was in front.
        var layoutChanged = LayoutChanged();
        var infoChanged = _continueInfo != LibrarySettings.ContinueInfo || _storeDetails != LibrarySettings.StoreDetails;
        if (_continuePlaying != LibrarySettings.ContinuePlaying || infoChanged || layoutChanged)
        {
            _continuePlaying = LibrarySettings.ContinuePlaying;
            _continueInfo = LibrarySettings.ContinueInfo;
            _storeDetails = LibrarySettings.StoreDetails;
            if (infoChanged)
            {
                _infoKey = null;
            }

            ApplyLayout();
        }

        ShowContinueArt();
        LoadSquares();
        UpdateRenderMode();
        LoadCardArt();

        // The deals kept on disk at once, and new ones if they are old and no game is running.
        UpdateDeals();
        _ = RefreshDealsAsync(force: false);

        // Switched in the main app or another session: the strip follows on the next look at the tiles.
        if (_showFilter != LibrarySettings.ShowFilter)
        {
            _showFilter = !_showFilter;
            ApplyFilter();
        }
        if (!_displayOff)
        {
            _clock.Start();
        }

        UpdateTopBar();
        _ = CheckForUpdateAsync();

        if (DateTime.UtcNow - _lastScan > RescanAfter)
        {
            _ = ScanAsync();
        }
        else
        {
            RestoreFocus();
        }

        // Back from a game the library cannot follow: whatever was changed for it is put back now.
        CheckUnfollowedSession();

        // Back in front of a game TDP Engine is holding: what it is doing, for a moment.
        _ = ShowAutoTdpAsync();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _gamepad.Stop();
        _clock.Stop();
        Notice = string.Empty;
        CloseMenu();

        // The open Not installed card holds a tile for every game the player owns and has not installed,
        // hundreds of them, which is about 90 MB that nothing needs behind a game. It goes back to the
        // card it grew from, and A opens it again on the game it was left on.
        if (IsBrowsing)
        {
            // The tile with the keyboard focus is about to be taken out of the window. Left holding
            // it, WPF would hand the focus to the window itself as the tile went, which makes the
            // library the active window again, in front of the game or store that just opened.
            Keyboard.ClearFocus();
            LeaveNotInstalled();
        }

        ClearBackdrop();
        ClearContinueArt();
        UnloadSquares();
        UnloadCardArt();
        UnloadNotInstalled();
        UnloadDeals();
        UpdateRenderMode();

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
        if (_allTiles.Count == 0)
        {
            EmptyText.Text = "Looking for your games...";
        }

        try
        {
            var previous = _allTiles;
            var (tiles, stats) = await Task.Run(() =>
            {
                var games = GameCatalog.Scan(refreshEmulators);
                GameFolders.Save(games);
                return (GameTile.ForScan(games, previous), Playtime.Read(GameCatalog.LastLaunched()));
            });
            _lastScan = DateTime.UtcNow;

            // Rebuilding the tiles resets scrolling, so only do it when something changed. New tiles take
            // their stars and hidden marks here; kept ones already have them.
            var marksChanged = MarkTiles(tiles);
            var statsChanged = !SameStats(stats, _stats);
            _stats = stats;
            if (marksChanged || statsChanged || !tiles.Select(t => t.Game).SequenceEqual(_allTiles.Select(t => t.Game)))
            {
                _allTiles = tiles;
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
        _ = FetchCoversAsync();
        _ = RefreshOwnedAsync(force: false);
    }

    /// <summary>
    /// A tab of the filter strip: every installed game, the favourites, one store's games, the games
    /// owned but not installed, or the apps. Apps are only ever on their own tab.
    /// </summary>
    private readonly record struct TileFilter(GameStore? Store, bool Favourites, bool NotInstalled = false, bool Apps = false)
    {
        public static readonly TileFilter All = default;
        public static readonly TileFilter Starred = new(null, true);
        public static readonly TileFilter Uninstalled = new(null, false, true);
        public static readonly TileFilter AppsTab = new(null, false, Apps: true);

        public string Name => Apps ? "Apps" : NotInstalled ? "Not installed" : Favourites ? "Favourites"
            : Store is { } store ? Game.NameOf(store) : "Games";

        public bool Shows(GameTile tile) => Apps ? tile.IsApp
            : NotInstalled ? !tile.IsInstalled
            : tile.IsInstalled && !tile.IsApp && (Favourites ? tile.IsFavourite : Store is not { } store || tile.Game.Store == store);
    }

    /// <summary>The games the grid can show: all but the hidden ones, unless those are shown too.</summary>
    private IEnumerable<GameTile> ShownTiles() => _showHidden ? _allTiles : _allTiles.Where(t => !t.IsHidden);

    /// <summary>
    /// The tabs of the filter strip, in order: Games; Apps, always, so there is somewhere to add them; then
    /// Favourites once a game is starred and a tab per store when games come from more than one, while the
    /// store filter is on; and last Not installed, whenever there are owned games that are not installed.
    /// </summary>
    private List<TileFilter> FilterOptions()
    {
        var shown = ShownTiles().Where(t => !t.IsApp).ToList();
        var installed = shown.Where(t => t.IsInstalled).ToList();
        var options = new List<TileFilter> { TileFilter.All, TileFilter.AppsTab };

        if (_showFilter)
        {
            if (installed.Any(t => t.IsFavourite))
            {
                options.Add(TileFilter.Starred);
            }

            var stores = installed.Select(t => t.Game.Store).Distinct().Order().ToList();
            if (stores.Count > 1)
            {
                options.AddRange(stores.Select(store => new TileFilter(store, false)));
            }
        }

        if (installed.Count < shown.Count)
        {
            options.Add(TileFilter.Uninstalled);
        }

        return options;
    }

    /// <summary>Shows the tiles of the current filter and rebuilds the filter strip to match the library.</summary>
    private void ApplyFilter()
    {
        var options = FilterOptions();

        // The filtered store's last game may just have been uninstalled, or the last favourite unstarred.
        if (!options.Contains(_filter))
        {
            _filter = TileFilter.All;
        }

        FilterTabs.Children.Clear();
        foreach (var option in options)
        {
            FilterTabs.Children.Add(FilterTab(option));
        }

        // Search is only on the Not installed tab, and is forgotten when that is left.
        SearchButton.Visibility = _filter.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
        if (!_filter.NotInstalled)
        {
            ResetSearch();
        }

        // The Not installed tab is a grid whatever the layout, so a change of tab may change how they are laid out.
        ApplyTilePanel();
        RefreshCategories();

        // Favourites first, each group in the chosen order.
        _tiles = Sorted(ShownTiles().Where(_filter.Shows).Where(Found))
            .OrderByDescending(t => t.IsFavourite).ToList();
        Tiles.ItemsSource = _tiles;
        AcceptHint.Text = AcceptText();
        UpdateOpenCardCount();

        // The pictures of games that are not installed are only kept while their tab is open.
        if (!_filter.NotInstalled)
        {
            UnloadNotInstalled();
        }

        UpdateContinue();
        UpdateDeals();
        UpdateEmptyText();
        LoadSquares();
        QueueCardReset();
    }

    private void UpdateEmptyText()
    {
        // Until the first scan is in, the text says the games are being looked for.
        if (_lastScan == DateTime.MinValue)
        {
            return;
        }

        // An Old School category with cards of its own is never empty.
        if (OpenKind is { } kind && kind != CategoryKind.Games)
        {
            EmptyText.Text = string.Empty;
            return;
        }

        EmptyText.Text = _search.Length > 0
            ? _tiles.Count == 0
                ? $"No games that are not installed match \"{_search}\"{(_searchStore is { } store ? $" on {Game.NameOf(store)}" : string.Empty)}."
                : string.Empty
            : _filter.Apps
            ? _tiles.Count == 0
                ? "No apps yet. Press Menu, then Games and apps, then Add a program."
                : string.Empty
            : _allTiles.All(t => !t.IsInstalled || t.IsApp) && !_filter.NotInstalled
            ? "No installed games were found in Steam, Xbox, Epic Games, Battle.net, GOG, the EA App or Ubisoft Connect.\nInstall a game, or press Menu, then Games and apps, then Add a program."
            : _tiles.Count == 0 ? "Every game is hidden. Turn on Show hidden games to bring them back. Press Menu, then Library display, then What it shows."
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
            IsChecked = filter == _filter
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
        if (filter == _filter)
        {
            return;
        }

        _filter = filter;

        // Another tab's Old School cards slide in, as they do when the layout is first shown.
        GameCards.Fresh = true;
        ApplyFilter();

        // From the Not installed grid's own tabs to a tab of games: that tab's category is the open one.
        OpenCategoryOfFilter();
        ShowCategoryCards();
        UpdateDeals();
        RestoreFocus();
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
        if (_layout == LibraryLayout.OldSchool)
        {
            LeaveNotInstalled();
            StepCategory(step > 0 ? 1 : -1);
            return;
        }

        var tabs = FilterOptions();
        var buttons = new List<Button>();
        if (SearchButton.Visibility == Visibility.Visible)
        {
            buttons.Add(SearchButton);
        }

        buttons.Add(SettingsButton);
        buttons.Add(PowerButton);

        // From a button, or from the open tab. The search bar and its store button count as the search button.
        var focused = Keyboard.FocusedElement as Button;
        var button = ReferenceEquals(focused, SearchBar) || ReferenceEquals(focused, StoreButton) ? SearchButton : focused;
        var at = button is not null && buttons.IndexOf(button) is >= 0 and var index ? tabs.Count + index : tabs.IndexOf(_filter);

        var count = tabs.Count + buttons.Count;
        var next = (((at + step) % count) + count) % count;
        if (next >= tabs.Count)
        {
            buttons[next - tabs.Count].Focus();
            return;
        }

        SetFilter(tabs[next]);
        RestoreFocus(leavingTopBar: true);
    }

    /// <summary>
    /// Puts the focus back on the game it was on. A rescan or a new cover must not pull the d-pad off the
    /// top bar or out of the search box, so the focus stays there unless it is the top bar asking to leave.
    /// </summary>
    private void RestoreFocus(bool leavingTopBar = false)
    {
        if (!IsActive || _menu is not null
            || (!leavingTopBar && (IsTopBarButton(Keyboard.FocusedElement) || IsSearching)))
        {
            return;
        }

        // An Old School category with cards of its own, not games: the card at the front of those.
        if (OpenKind is { } kind && kind != CategoryKind.Games)
        {
            FocusFrontCard();
            return;
        }

        if (_tiles.Count == 0)
        {
            return;
        }

        // Wait for the item containers to exist after ItemsSource changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Back on the deal it was on, when that deal is still listed.
            if (RestoreDealFocus())
            {
                return;
            }

            // Back on the Last played card it was on, when that game is still there.
            if (_focusInContinue && ContinuePanel.Visibility == Visibility.Visible
                && ContinueCard.DataContext is GameTile card && card.Game.Key == _focusedKey)
            {
                ContinueCard.Focus();
                QueueBackdrop(card);
                return;
            }

            var index = Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey));
            FocusTile(index);

            // A tile that kept the focus raises no focus event, and its background (or its pictures, for a
            // game that is not installed) may have been released.
            if (index < _tiles.Count)
            {
                QueueBackdrop(_tiles[index]);
                LoadNotInstalledNear(index);
            }
        });
    }

    private void Launch(GameTile tile)
    {
        if (DateTime.UtcNow < _launchBlockedUntil)
        {
            return;
        }

        _focusedKey = tile.Game.Key;

        // An app opens beside the game, which stays as it is, paused or not. Apps never have a session,
        // so they are never paused themselves.
        if (tile.IsInstalled && !tile.IsApp && _session is { } session)
        {
            // The game being played, paused or not: back into it.
            if (session.StartedAt is not null && session.Game.Key == tile.Game.Key)
            {
                _launchBlockedUntil = DateTime.UtcNow + LaunchCooldown;
                _ = BackToGameAsync(session, tile);
                return;
            }

            // Only one game is kept paused.
            if (session.IsPaused)
            {
                ConfirmClosePaused(session, tile);
                return;
            }
        }

        StartGame(tile);
    }

    private void StartGame(GameTile tile)
    {
        _launchBlockedUntil = DateTime.UtcNow + LaunchCooldown;
        _focusedKey = tile.Game.Key;
        _closeArmed = null;

        // A game that is not installed opens its store's install page, with nothing else to set up.
        if (!tile.IsInstalled)
        {
            var problem = GameCatalog.Launch(tile.Game);
            ShowNotice(problem ?? $"Opening {Game.NameOf(tile.Game.Store)} to install {tile.Title}...", tile);
            if (problem is not null)
            {
                _launchBlockedUntil = DateTime.MinValue;
            }

            return;
        }

        // An app just opens. Whatever is set up for a game that is running stays as it is.
        if (tile.IsApp)
        {
            var failed = GameCatalog.Launch(tile.Game);
            ShowNotice(failed ?? $"Opening {tile.Title}...", tile);
            if (failed is not null)
            {
                _launchBlockedUntil = DateTime.MinValue;
            }

            return;
        }

        // The last game's profile never carries over to the next game.
        RestoreProfileSettings();
        var profile = GameProfiles.For(tile.Game);
        var storeWasOpen = StoreClients.IsRunning(tile.Game.Store);

        var error = GameCatalog.Launch(tile.Game);
        ShowNotice(error ?? $"Launching {tile.Title}...", tile, error is null ? LaunchNoticeShownFor : null);
        if (error is not null)
        {
            _launchBlockedUntil = DateTime.MinValue;
            return;
        }

        // The last game's play time is counted up to now. Programs closed for it stay closed for this one.
        StopFollowing();

        if (!profile.IsEmpty)
        {
            ApplyProfile(tile.Game, profile);
        }

        StartSession(tile.Game, storeWasOpen, profile.CloseApps ?? LibrarySettings.GameMode,
            profile.AutoTdpFps ?? LibrarySettings.AutoTdpFps, profile.FanCurve ?? LibrarySettings.FanCurve);
    }
}
