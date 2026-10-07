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

    // The tiles on screen: all of them, or one store's when a filter is on.
    private List<GameTile> _tiles = [];
    private GameStore? _filter;

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
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _gamepad.Stop();
        _clock.Stop();
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
            var tiles = await Task.Run(() => GameCatalog.Scan().Select(GameTile.Create).ToList());
            _lastScan = DateTime.UtcNow;

            // Rebuilding the tiles resets scrolling, so only do it when something changed.
            if (!tiles.Select(t => t.Game).SequenceEqual(_allTiles.Select(t => t.Game)))
            {
                _allTiles = tiles;
                ApplyFilter();
            }

            EmptyText.Text = _allTiles.Count == 0
                ? "No installed games were found in Steam, Xbox, Epic Games, Battle.net, GOG, the EA App or Ubisoft Connect.\nInstall a game, then press Y to refresh."
                : string.Empty;
        }
        finally
        {
            _scanning = false;
        }

        RestoreFocus();
        _ = FetchCoversAsync();
    }

    /// <summary>Stores with at least one installed game, in the order their tabs are shown.</summary>
    private List<GameStore> InstalledStores() => _allTiles.Select(t => t.Game.Store).Distinct().Order().ToList();

    /// <summary>Shows the tiles of the current filter and rebuilds the filter strip to match the library.</summary>
    private void ApplyFilter()
    {
        var stores = InstalledStores();
        var filterable = _showFilter && stores.Count > 1;

        // The filtered store's last game may just have been uninstalled.
        if (!filterable || (_filter is { } current && !stores.Contains(current)))
        {
            _filter = null;
        }

        FilterStrip.Visibility = filterable ? Visibility.Visible : Visibility.Collapsed;
        FilterTabs.Children.Clear();
        if (filterable)
        {
            FilterTabs.Children.Add(FilterTab("All", null));
            foreach (var store in stores)
            {
                FilterTabs.Children.Add(FilterTab(Game.NameOf(store), store));
            }
        }

        _tiles = _filter is { } shown ? _allTiles.Where(t => t.Game.Store == shown).ToList() : _allTiles;
        Tiles.ItemsSource = _tiles;
        CountText.Text = _tiles.Count == 1 ? "1 game" : $"{_tiles.Count} games";
    }

    private RadioButton FilterTab(string name, GameStore? store)
    {
        var tab = new RadioButton
        {
            Style = (Style)FindResource("FilterTab"),
            GroupName = "StoreFilter",
            Content = name,
            Tag = store,
            IsChecked = store == _filter
        };

        tab.Click += (_, _) => SetFilter(store);
        return tab;
    }

    private void SetFilter(GameStore? store)
    {
        if (store == _filter)
        {
            return;
        }

        _filter = store;
        ApplyFilter();
        RestoreFocus();
    }

    /// <summary>LB and RB: one tab left or right, wrapping around.</summary>
    private void CycleFilter(int step)
    {
        var stores = InstalledStores();
        if (!_showFilter || stores.Count < 2)
        {
            return;
        }

        var options = new List<GameStore?> { null };
        options.AddRange(stores.Cast<GameStore?>());
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

        var error = GameCatalog.Launch(tile.Game);
        StatusText.Text = error ?? $"Starting {tile.Title}...";
        if (error is not null)
        {
            _launchBlockedUntil = DateTime.MinValue;
        }
    }
}
