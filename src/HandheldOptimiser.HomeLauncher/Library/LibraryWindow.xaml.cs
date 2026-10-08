using System.Windows;
using System.Windows.Controls;
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

    // The tiles on screen: all of them, the favourites, or one store's when a filter is on.
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
        Loaded += (_, _) => WatchScreens();
        SourceInitialized += (_, _) => WatchWake();
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnKeyDown;
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
        // Just woken with a game running: straight back to it. Otherwise the game is paused, with Quick Resume.
        _ = ResumeGameAsync();
        if (!_resuming && !JustWoke)
        {
            PauseOnReturn();
        }

        _gamepad.Start();
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
        if (_continuePlaying != LibrarySettings.ContinuePlaying || layoutChanged)
        {
            _continuePlaying = LibrarySettings.ContinuePlaying;
            ApplyLayout();
        }

        ShowContinueArt();

        // Switched in the main app or another session: the strip follows on the next look at the tiles.
        if (_showFilter != LibrarySettings.ShowFilter)
        {
            _showFilter = !_showFilter;
            ApplyFilter();
        }
        _clock.Start();
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
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _gamepad.Stop();
        _clock.Stop();
        StatusText.Text = string.Empty;
        CloseMenu();
        ClearBackdrop();
        ClearContinueArt();
        UnloadNotInstalled();

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
            EmptyText.Text = "Finding your games...";
        }

        try
        {
            var previous = _allTiles;
            var (tiles, stats) = await Task.Run(() =>
            {
                var games = GameCatalog.Scan(refreshEmulators);
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
        finally
        {
            _scanning = false;
        }

        RestoreFocus();
        _ = FetchCoversAsync();
        _ = RefreshOwnedAsync(force: false);
    }

    /// <summary>
    /// A tab of the filter strip: every installed game, the favourites, one store's games, or the games
    /// owned but not installed.
    /// </summary>
    private readonly record struct TileFilter(GameStore? Store, bool Favourites, bool NotInstalled = false)
    {
        public static readonly TileFilter All = default;
        public static readonly TileFilter Starred = new(null, true);
        public static readonly TileFilter Uninstalled = new(null, false, true);

        public string Name => NotInstalled ? "Not installed" : Favourites ? "Favourites" : Store is { } store ? Game.NameOf(store) : "All";

        public bool Shows(GameTile tile) => NotInstalled ? !tile.IsInstalled
            : tile.IsInstalled && (Favourites ? tile.IsFavourite : Store is not { } store || tile.Game.Store == store);
    }

    /// <summary>The games the grid can show: all but the hidden ones, unless those are shown too.</summary>
    private IEnumerable<GameTile> ShownTiles() => _showHidden ? _allTiles : _allTiles.Where(t => !t.IsHidden);

    /// <summary>
    /// The tabs of the filter strip, in order, or none when it would only have All: Favourites once a game
    /// is starred, and a tab per store when games come from more than one, while the store filter is on;
    /// and Not installed, whenever there are owned games that are not installed.
    /// </summary>
    private List<TileFilter> FilterOptions()
    {
        var shown = ShownTiles().ToList();
        var installed = shown.Where(t => t.IsInstalled).ToList();
        var options = new List<TileFilter> { TileFilter.All };

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

        return options.Count > 1 ? options : [];
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

        FilterStrip.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterTabs.Children.Clear();
        foreach (var option in options)
        {
            FilterTabs.Children.Add(FilterTab(option));
        }

        // Favourites first, each group in the chosen order.
        _tiles = Sorted(ShownTiles().Where(_filter.Shows)).OrderByDescending(t => t.IsFavourite).ToList();
        Tiles.ItemsSource = _tiles;
        CountText.Text = _filter.NotInstalled ? $"{_tiles.Count} not installed"
            : _tiles.Count == 1 ? "1 game"
            : $"{_tiles.Count} games";
        AcceptHint.Text = _filter.NotInstalled ? "Install" : "Play";

        // The pictures of games that are not installed are only kept while their tab is open.
        if (!_filter.NotInstalled)
        {
            UnloadNotInstalled();
        }

        UpdateContinue();
        UpdateEmptyText();
    }

    private void UpdateEmptyText()
    {
        // Until the first scan is in, the text says the games are being looked for.
        if (_lastScan == DateTime.MinValue)
        {
            return;
        }

        EmptyText.Text = _allTiles.All(t => !t.IsInstalled) && !_filter.NotInstalled
            ? "No installed games were found in Steam, Xbox, Epic Games, Battle.net, GOG, the EA App or Ubisoft Connect.\nInstall a game or add a program under Settings, then press Y and choose Refresh library."
            : _tiles.Count == 0 ? "Every game is hidden. Switch on Show hidden games under Settings, Display to bring them back."
            : string.Empty;
    }

    private RadioButton FilterTab(TileFilter filter)
    {
        var tab = new RadioButton
        {
            Style = (Style)FindResource("FilterTab"),
            GroupName = "StoreFilter",
            Content = filter.Name,
            IsChecked = filter == _filter
        };

        tab.Click += (_, _) => SetFilter(filter);
        return tab;
    }

    private void SetFilter(TileFilter filter)
    {
        if (filter == _filter)
        {
            return;
        }

        _filter = filter;
        ApplyFilter();
        RestoreFocus();
    }

    /// <summary>LB and RB: one tab left or right, wrapping around.</summary>
    private void CycleFilter(int step)
    {
        var options = FilterOptions();
        if (options.Count < 2)
        {
            return;
        }

        SetFilter(options[(options.IndexOf(_filter) + step + options.Count) % options.Count]);
    }

    private void RestoreFocus()
    {
        if (!IsActive || _tiles.Count == 0 || _menu is not null)
        {
            return;
        }

        // Wait for the item containers to exist after ItemsSource changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Back on the Continue playing card or tile it was on, when that game is still there.
            if (_focusInContinue && ContinueButtons().Find(b => b.DataContext is GameTile t && t.Game.Key == _focusedKey) is { } button)
            {
                button.Focus();
                QueueBackdrop((GameTile)button.DataContext);
                return;
            }

            var index = Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey));
            FocusTile(index);

            // A tile that kept the focus raises no focus event, and its background (or its pictures, for a
            // game that is not installed) may have been released.
            if (index < _tiles.Count)
            {
                QueueBackdrop(_tiles[index]);
                ShowRowDetails(_tiles[index]);
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
        if (tile.IsInstalled && _session is { } session)
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
            StatusText.Text = problem ?? $"Opening {Game.NameOf(tile.Game.Store)} to install {tile.Title}...";
            if (problem is not null)
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
        StatusText.Text = error ?? $"Starting {tile.Title}...";
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

        StartSession(tile.Game, storeWasOpen, profile.CloseApps ?? LibrarySettings.GameMode);
    }
}
