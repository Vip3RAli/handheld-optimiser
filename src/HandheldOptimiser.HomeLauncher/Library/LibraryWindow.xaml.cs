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

    // Looks now and then for a profiled game having closed, while the library is in front.
    private readonly DispatcherTimer _profileCheck;
    private List<GameTile> _allTiles = [];

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

        _profileCheck = new DispatcherTimer(ProfileCheckEvery, DispatcherPriority.Background, (_, _) => OnProfileCheck(), Dispatcher);
        _profileCheck.Stop();

        // Closing the library also ends what a game's profile changed.
        Closed += (_, _) => RestoreProfileSettings();

        // Scan on load, not only on activation: Windows can start the home app without giving it focus.
        Loaded += (_, _) => _ = ScanAsync();
        Loaded += (_, _) => _ = CheckForUpdateAsync();
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>Brings the library to the front; called when the home button starts a second instance.</summary>
    public void ShowLibrary()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Maximized;
        }

        Show();
        Activate();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _gamepad.Start();
        _background = LibrarySettings.Background;
        _strength = LibrarySettings.Strength;
        _blur = LibrarySettings.Blur;
        _position = LibrarySettings.Position;
        _showStatus = LibrarySettings.ShowStatus;
        _quickActions = LibrarySettings.QuickActions;
        _showHidden = LibrarySettings.ShowHidden;
        OptionsHint.Visibility = _quickActions ? Visibility.Visible : Visibility.Collapsed;

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

        // Back from a game played with its own profile: put the player's settings back once it has closed.
        _ = CheckProfileRestoreAsync();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _gamepad.Stop();
        _clock.Stop();
        _profileCheck.Stop();
        StatusText.Text = string.Empty;
        CloseMenu();
        ClearBackdrop();

        // Once the rendering of the focus change has gone out, nothing here is needed until we are back.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Native.TrimWorkingSet);
    }

    private async Task ScanAsync()
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
            var tiles = await Task.Run(() => GameTile.ForScan(GameCatalog.Scan(), previous));
            _lastScan = DateTime.UtcNow;

            // Rebuilding the tiles resets scrolling, so only do it when something changed. New tiles take
            // their stars and hidden marks here; kept ones already have them.
            var marksChanged = MarkTiles(tiles);
            if (marksChanged || !tiles.Select(t => t.Game).SequenceEqual(_allTiles.Select(t => t.Game)))
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
    }

    /// <summary>A tab of the filter strip: every game, the favourites, or one store's games.</summary>
    private readonly record struct TileFilter(GameStore? Store, bool Favourites)
    {
        public static readonly TileFilter All = default;
        public static readonly TileFilter Starred = new(null, true);

        public string Name => Favourites ? "Favourites" : Store is { } store ? Game.NameOf(store) : "All";

        public bool Shows(GameTile tile) => Favourites ? tile.IsFavourite : Store is not { } store || tile.Game.Store == store;
    }

    /// <summary>The games the grid can show: all but the hidden ones, unless those are shown too.</summary>
    private IEnumerable<GameTile> ShownTiles() => _showHidden ? _allTiles : _allTiles.Where(t => !t.IsHidden);

    /// <summary>
    /// The tabs of the filter strip, in order, or none when the strip is off or would only have All:
    /// Favourites once a game is starred, and a tab per store when games come from more than one.
    /// </summary>
    private List<TileFilter> FilterOptions()
    {
        if (!_showFilter)
        {
            return [];
        }

        var shown = ShownTiles().ToList();
        var options = new List<TileFilter> { TileFilter.All };
        if (shown.Any(t => t.IsFavourite))
        {
            options.Add(TileFilter.Starred);
        }

        var stores = shown.Select(t => t.Game.Store).Distinct().Order().ToList();
        if (stores.Count > 1)
        {
            options.AddRange(stores.Select(store => new TileFilter(store, false)));
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

        // Favourites first, each group still in the order the scan gave: last played first.
        _tiles = ShownTiles().Where(_filter.Shows).OrderByDescending(t => t.IsFavourite).ToList();
        Tiles.ItemsSource = _tiles;
        CountText.Text = _tiles.Count == 1 ? "1 game" : $"{_tiles.Count} games";
        UpdateEmptyText();
    }

    private void UpdateEmptyText()
    {
        // Until the first scan is in, the text says the games are being looked for.
        if (_lastScan == DateTime.MinValue)
        {
            return;
        }

        EmptyText.Text = _allTiles.Count == 0
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
            var index = Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey));
            FocusTile(index);

            // A tile that kept the focus raises no focus event, and its background may have been released.
            if (index < _tiles.Count)
            {
                QueueBackdrop(_tiles[index]);
            }
        });
    }

    private void Launch(GameTile tile)
    {
        if (DateTime.UtcNow < _launchBlockedUntil)
        {
            return;
        }

        _launchBlockedUntil = DateTime.UtcNow + LaunchCooldown;
        _focusedKey = tile.Game.Key;

        // The last profiled game's settings never carry over to the next game.
        RestoreProfileSettings();
        var profile = GameProfiles.For(tile.Game);

        var error = GameCatalog.Launch(tile.Game);
        StatusText.Text = error ?? $"Starting {tile.Title}...";
        if (error is not null)
        {
            _launchBlockedUntil = DateTime.MinValue;
        }
        else if (!profile.IsEmpty)
        {
            ApplyProfile(tile.Game, profile);
        }
    }
}
