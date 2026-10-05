using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // Inside width of the battery outline, which the fill is a share of.
    private const double BatteryFillWidth = 22;
    private const int LowBatteryPercent = 20;

    private static readonly Brush NormalBattery = FrozenBrush("#F2F3F5");
    private static readonly Brush LowBattery = FrozenBrush("#E5534B");

    private readonly GamepadInput _gamepad;
    private readonly DispatcherTimer _clock;
    private List<GameTile> _allTiles = [];

    // The tiles on screen: all of them, or one store's when a filter is on.
    private List<GameTile> _tiles = [];
    private GameStore? _filter;

    // Long enough that holding a direction across a row does not load every game passed on the way.
    private static readonly TimeSpan BackdropDelay = TimeSpan.FromMilliseconds(150);
    private static readonly Duration BackdropFade = TimeSpan.FromMilliseconds(200);

    private enum MenuEditor
    {
        Arguments,
        ArtworkTitle,
        ArtworkKey
    }

    // The list showing in the overlay: a game's quick actions, the library's settings or the power menu.
    // Null when closed.
    private StackPanel? _menu;

    // The Restart or Shut down row that has been pressed once and acts on the next press.
    private Button? _confirming;

    // The game the quick actions are for, and which text setting is being typed.
    private GameTile? _menuTile;
    private MenuEditor? _editing;

    // The game whose colours or artwork are behind the grid, or on their way there.
    private string? _backdropKey;
    private CancellationTokenSource? _backdropLoad;

    // The player's choices, from the settings menu here or the main app.
    private BackgroundKind _background = LibrarySettings.Background;
    private ColourStrength _strength = LibrarySettings.Strength;
    private ArtworkBlur _blur = LibrarySettings.Blur;
    private ArtworkPosition _position = LibrarySettings.Position;
    private bool _showStatus = LibrarySettings.ShowStatus;
    private bool _showFilter = LibrarySettings.ShowFilter;
    private bool _quickActions = LibrarySettings.QuickActions;

    // The library stays open for days, so it looks for a newer release again now and then.
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    // The newer release a check found, and the one whose banner the player closed.
    private Version? _update;
    private Version? _dismissedUpdate;

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

    private async Task CheckForUpdateAsync()
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckEvery)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;
        if (await Updates.CheckAsync() is { } newer)
        {
            _update = newer;
            UpdateText.Text = $"Handheld Optimiser {Updates.Display(newer)} is available";
            UpdateBanner.Visibility = newer == _dismissedUpdate ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>From the banner or the settings row. The main app does the installing, and reopens when done.</summary>
    private void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        StatusText.Text = Updates.StartUpdate() ?? "Opening Handheld Optimiser to install the update...";
    }

    /// <summary>Hides the banner for this release. The update stays in Settings.</summary>
    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        _dismissedUpdate = _update;
        UpdateBanner.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// Puts the game's colours, or its artwork when the player chose that, behind the grid once the
    /// focus has settled on it. Artwork falls back to the colours for a game with none.
    /// </summary>
    private void QueueBackdrop(GameTile tile)
    {
        if (tile.Game.Key == _backdropKey || !IsActive)
        {
            return;
        }

        _backdropKey = tile.Game.Key;
        _backdropLoad?.Cancel();
        _backdropLoad = new CancellationTokenSource();
        _ = LoadBackdropAsync(tile, _backdropLoad.Token);
    }

    /// <summary>Shows the focused game's background again, after a setting that affects it changed.</summary>
    private void ReloadBackdrop()
    {
        _backdropKey = null;
        if (_tiles.Find(t => t.Game.Key == _focusedKey) is { } tile)
        {
            QueueBackdrop(tile);
        }
    }

    private async Task LoadBackdropAsync(GameTile tile, CancellationToken superseded)
    {
        try
        {
            await Task.Delay(BackdropDelay, superseded);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var background = _background;
        var blur = _blur;
        var position = _position;
        var (width, height) = BackdropPixels();
        var (from, to) = tile.BackdropColours;
        var shade = _strength switch
        {
            ColourStrength.Subtle => 1,
            ColourStrength.Strong => 0.55,
            _ => 0.7
        };

        // Built at the screen's own size off the UI thread, so showing it is only a copy.
        var backdrop = await Task.Run(async () =>
        {
            if (background == BackgroundKind.Plain)
            {
                return null;
            }

            if (background == BackgroundKind.Artwork && await Artwork.FindAsync(tile.Game) is { } path
                && Backdrops.FromArtwork(path, width, height, blur, position, from, to, shade) is { } artwork)
            {
                // Decoding artwork (up to 4K) leaves tens of MB of buffers behind, some of them outside
                // the managed heap where only a collection frees them. Left alone they pile up with
                // every game passed, in a process that is meant to stay small.
                GC.Collect();
                return artwork;
            }

            return Backdrops.Gradient(from, to, width, height, shade);
        });

        if (!superseded.IsCancellationRequested)
        {
            ShowBackdrop(backdrop);
        }
    }

    /// <summary>The backdrop layer's size in real pixels, which is what the picture is made at.</summary>
    private (int Width, int Height) BackdropPixels()
    {
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = (int)Math.Round(BackdropFront.ActualWidth * toDevice.M11);
        var height = (int)Math.Round(BackdropFront.ActualHeight * toDevice.M22);

        // Before the first layout pass there is no size yet; any sensible one will do until the next.
        return width > 0 && height > 0 ? (width, height) : (1920, 1080);
    }

    /// <summary>
    /// Fades the new background in over the old, which stays solid underneath: one see-through layer
    /// costs far less to draw than two. Null fades to the plain background.
    /// </summary>
    private void ShowBackdrop(ImageSource? backdrop)
    {
        Brush? brush = null;
        if (backdrop is not null)
        {
            brush = new ImageBrush(backdrop) { Stretch = Stretch.Fill };
            brush.Freeze();
        }

        var previous = BackdropFront.Fill;
        if (brush is null && previous is null)
        {
            return;
        }

        BackdropBack.BeginAnimation(OpacityProperty, null);
        BackdropFront.BeginAnimation(OpacityProperty, null);

        if (brush is null)
        {
            // Nothing new to show: the old one fades away on the back layer.
            BackdropFront.Fill = null;
            BackdropFront.Opacity = 0;
            BackdropBack.Fill = previous;
            BackdropBack.BeginAnimation(OpacityProperty, Fade(1, 0, () => BackdropBack.Fill = null));
            return;
        }

        BackdropBack.Fill = previous;
        BackdropBack.Opacity = previous is null ? 0 : 1;
        BackdropFront.Fill = brush;

        // Once the new one is solid the old one is covered, and can go.
        BackdropFront.BeginAnimation(OpacityProperty, Fade(0, 1, () =>
        {
            if (ReferenceEquals(BackdropFront.Fill, brush))
            {
                BackdropBack.Fill = null;
                BackdropBack.Opacity = 0;
            }
        }));
    }

    private static DoubleAnimation Fade(double from, double to, Action done)
    {
        var fade = new DoubleAnimation(from, to, BackdropFade);

        // Software rendering redraws the whole window for each step, so take fewer of them.
        Timeline.SetDesiredFrameRate(fade, 30);
        fade.Completed += (_, _) => done();
        return fade;
    }

    /// <summary>Lets go of the background while a game is in front, along with the rest of the memory.</summary>
    private void ClearBackdrop()
    {
        _backdropLoad?.Cancel();
        _backdropKey = null;

        foreach (var layer in new[] { BackdropBack, BackdropFront })
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
            layer.Fill = null;
        }
    }

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
                CloseMenu();
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

    private void OpenMenu(GameTile tile)
    {
        var game = tile.Game;
        _menuTile = tile;
        _focusedKey = game.Key;
        _menu = GameItems;
        SettingsItems.Visibility = Visibility.Collapsed;
        PowerItems.Visibility = Visibility.Collapsed;

        MenuTitle.Text = tile.Title;
        MenuStore.Text = tile.StoreName;

        var takesArguments = GameCatalog.SupportsCustomArguments(game);
        ArgumentsItem.IsEnabled = takesArguments;
        ArgumentsItem.Tag = !takesArguments ? $"Not available for {game.StoreName} games"
            : GameCatalog.CustomArguments(game) is { Length: > 0 } arguments ? arguments
            : "None";

        var folder = game.InstallDirectory is { } dir && Directory.Exists(dir) ? dir : null;
        FolderItem.IsEnabled = folder is not null;
        FolderItem.Tag = folder ?? "Install folder not found";

        // Steam does not record which exe is the game, so its games show the folder's properties.
        var exe = game.ExecutablePath is { } path && File.Exists(path) ? path : null;
        PropertiesItem.IsEnabled = (exe ?? folder) is not null;
        PropertiesItem.Content = exe is null && folder is not null ? "Install folder properties" : "Executable properties";
        PropertiesItem.Tag = exe ?? folder ?? "Executable not found";

        var canLookUp = Artwork.HasApiKey;
        ArtworkItem.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkItem.IsEnabled = canLookUp;
        ArtworkItem.Tag = !canLookUp ? "Add a SteamGridDB key in Settings to change this"
            : Artwork.SearchTitle(game) is { } corrected ? $"Looked up as \"{corrected}\""
            : game.HeroPath is not null ? "Steam's own artwork. Enter a title to look it up instead"
            : "Looked up by the game's own title";

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    /// <summary>The library's own settings, from the gear in the top bar or the controller's Menu button.</summary>
    private void OpenSettings()
    {
        _menuTile = null;
        _menu = SettingsItems;
        GameItems.Visibility = Visibility.Collapsed;
        PowerItems.Visibility = Visibility.Collapsed;

        MenuTitle.Text = "Settings";
        MenuStore.Text = "Game library";
        RefreshSettings();

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    /// <summary>The power menu, from the power button in the top bar or the controller's View button.</summary>
    private void OpenPower()
    {
        _menuTile = null;
        _menu = PowerItems;
        GameItems.Visibility = Visibility.Collapsed;
        SettingsItems.Visibility = Visibility.Collapsed;

        MenuTitle.Text = "Power";
        MenuStore.Text = "This device";

        // Asked each time, so switching hibernate on or off in Windows shows here without a restart.
        HibernateItem.Visibility = Power.CanHibernate ? Visibility.Visible : Visibility.Collapsed;
        _confirming = null;
        RefreshPower();

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    private void RefreshPower()
    {
        const string confirm = "Press again to confirm";
        RestartItem.Tag = ReferenceEquals(_confirming, RestartItem) ? confirm : "Close everything and start Windows again";
        ShutDownItem.Tag = ReferenceEquals(_confirming, ShutDownItem) ? confirm : "Close everything and power off";
    }

    private void OnOpenPower(object sender, RoutedEventArgs e) => OpenPower();

    private async void OnSleep(object sender, RoutedEventArgs e)
    {
        // Closed first, so the device does not wake up on the power menu.
        CloseMenu();
        Program.Log("Sleep chosen in the power menu");

        // Off the UI thread: the call does not return until the device is awake again.
        var window = new WindowInteropHelper(this).Handle;
        ReportPower(await Task.Run(() => Power.Sleep(window)));
    }

    private async void OnHibernate(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Program.Log("Hibernate chosen in the power menu");
        ReportPower(await Task.Run(Power.Hibernate));
    }

    private void OnRestart(object sender, RoutedEventArgs e) => ConfirmPower(RestartItem, "Restart", Power.Restart);

    private void OnShutDown(object sender, RoutedEventArgs e) => ConfirmPower(ShutDownItem, "Shut down", Power.ShutDown);

    /// <summary>Arms a row on its first press and acts on its second.</summary>
    private void ConfirmPower(Button row, string name, Func<string?> action)
    {
        if (!ReferenceEquals(_confirming, row))
        {
            _confirming = row;
            RefreshPower();
            return;
        }

        CloseMenu();
        Program.Log($"{name} chosen in the power menu");
        ReportPower(action());
    }

    /// <summary>Moving off an armed row disarms it, so the second press is always a deliberate one.</summary>
    private void OnPowerRowLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(_confirming, sender))
        {
            _confirming = null;
            RefreshPower();
        }
    }

    private void ReportPower(string? error)
    {
        if (error is not null)
        {
            Program.Log(error);
            StatusText.Text = error;
        }
    }

    private void RefreshSettings()
    {
        UpdateSetting.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSetting.Tag = _update is null ? null : $"Handheld Optimiser {Updates.Display(_update)} is available";

        BackgroundSetting.Tag = _background switch
        {
            BackgroundKind.Artwork => "Game artwork",
            BackgroundKind.Plain => "Plain",
            _ => "Game colours"
        };

        // Artwork mode still shows colours for a game with no artwork, so the strength applies there too.
        StrengthSetting.IsEnabled = _background != BackgroundKind.Plain;
        StrengthSetting.Tag = _strength.ToString();

        BlurSetting.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        BlurSetting.Tag = _blur.ToString();

        PositionSetting.Visibility = BlurSetting.Visibility;
        PositionSetting.Tag = _position.ToString();

        StatusSetting.Tag = OnOff(_showStatus);
        FilterSetting.Tag = OnOff(_showFilter);
        QuickActionsSetting.Tag = OnOff(_quickActions);

        // Only the end of the key is shown: enough to tell which one it is.
        ArtworkKeySetting.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkKeySetting.Tag = LibrarySettings.ArtworkKey is not { } key ? "Not set. Steam games still show their artwork"
            : key.Length > 4 ? $"Set, ending in {key[^4..]}"
            : "Set";
    }

    private static string OnOff(bool on) => on ? "On" : "Off";

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnToggleStatus(object sender, RoutedEventArgs e)
    {
        _showStatus = !_showStatus;
        LibrarySettings.ShowStatus = _showStatus;
        RefreshSettings();
        _ = UpdateStatusAsync();
    }

    private void OnToggleFilter(object sender, RoutedEventArgs e)
    {
        _showFilter = !_showFilter;
        LibrarySettings.ShowFilter = _showFilter;
        RefreshSettings();

        // Switching it off also drops a store filter that could no longer be changed.
        ApplyFilter();
    }

    private void OnToggleQuickActions(object sender, RoutedEventArgs e)
    {
        _quickActions = !_quickActions;
        LibrarySettings.QuickActions = _quickActions;
        OptionsHint.Visibility = _quickActions ? Visibility.Visible : Visibility.Collapsed;
        RefreshSettings();
    }

    private void OnCycleBackground(object sender, RoutedEventArgs e) => StepBackground(1);

    private void OnCycleStrength(object sender, RoutedEventArgs e) => StepStrength(1);

    private void OnCycleBlur(object sender, RoutedEventArgs e) => StepBlur(1);

    private void OnCyclePosition(object sender, RoutedEventArgs e) => StepPosition(1);

    /// <summary>Closes the library. Windows starts it again on the next home button press.</summary>
    private void OnQuit(object sender, RoutedEventArgs e)
    {
        Program.Log("Game library closed from its settings");
        Close();
    }

    private void StepBackground(int step)
    {
        _background = Step(_background, step);
        LibrarySettings.Background = _background;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepStrength(int step)
    {
        _strength = Step(_strength, step);
        LibrarySettings.Strength = _strength;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepPosition(int step)
    {
        _position = Step(_position, step);
        LibrarySettings.Position = _position;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepBlur(int step)
    {
        _blur = Step(_blur, step);
        LibrarySettings.Blur = _blur;
        RefreshSettings();
        ReloadBackdrop();
    }

    /// <summary>The next or previous choice of a setting, going round at either end.</summary>
    private static T Step<T>(T value, int step) where T : struct, Enum
    {
        var choices = Enum.GetValues<T>();
        return choices[(Array.IndexOf(choices, value) + step + choices.Length) % choices.Length];
    }

    /// <summary>Left and right on a settings row with several choices step through them, like a slider.</summary>
    private bool StepFocusedSetting(int step)
    {
        if (!ReferenceEquals(_menu, SettingsItems) || Keyboard.FocusedElement is not Button row)
        {
            return false;
        }

        if (ReferenceEquals(row, BackgroundSetting))
        {
            StepBackground(step);
        }
        else if (ReferenceEquals(row, StrengthSetting))
        {
            StepStrength(step);
        }
        else if (ReferenceEquals(row, PositionSetting))
        {
            StepPosition(step);
        }
        else if (ReferenceEquals(row, BlurSetting))
        {
            StepBlur(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void OnEditArtworkKey(object sender, RoutedEventArgs e) =>
        BeginEdit(MenuEditor.ArtworkKey, "SteamGridDB API key",
            "From steamgriddb.com, under Preferences then API. It fetches artwork for games outside Steam, whose titles are sent to SteamGridDB to find it. Leave empty to remove the key.",
            LibrarySettings.ArtworkKey);

    /// <summary>The open list of rows, which is also where cancelling the text box goes back to.</summary>
    private void ShowMenuItems()
    {
        if (_menu is null)
        {
            return;
        }

        _editing = null;
        TextEditor.Visibility = Visibility.Collapsed;
        _menu.Visibility = Visibility.Visible;

        // Wait for the rows to be laid out before one can take focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => _menu?.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled && b.IsVisible)?.Focus());
    }

    private void CloseMenu()
    {
        if (_menu is null)
        {
            return;
        }

        _menu = null;
        _menuTile = null;
        _editing = null;
        _confirming = null;
        MenuOverlay.Visibility = Visibility.Collapsed;
        RestoreFocus();
    }

    private void MoveMenuFocus(FocusNavigationDirection direction)
    {
        if (direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
        {
            StepFocusedSetting(direction == FocusNavigationDirection.Right ? 1 : -1);
            return;
        }

        var step = direction switch
        {
            FocusNavigationDirection.Up => -1,
            FocusNavigationDirection.Down => 1,
            _ => 0
        };

        var items = _menu?.Children.OfType<Button>().Where(b => b.IsEnabled && b.IsVisible).ToList() ?? [];
        if (step == 0 || items.Count == 0)
        {
            return;
        }

        var index = Keyboard.FocusedElement is Button focused ? items.IndexOf(focused) : -1;
        items[Math.Clamp(index + step, 0, items.Count - 1)].Focus();
    }

    private void OnMenuBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MenuOverlay))
        {
            CloseMenu();
        }
    }

    private void OnEditArguments(object sender, RoutedEventArgs e)
    {
        if (_menuTile is { } tile)
        {
            BeginEdit(MenuEditor.Arguments, "Launch arguments",
                "Added to the end of the command that starts this game. Leave empty for none.",
                GameCatalog.CustomArguments(tile.Game));
        }
    }

    private void OnEditArtworkTitle(object sender, RoutedEventArgs e)
    {
        if (_menuTile is { } tile)
        {
            BeginEdit(MenuEditor.ArtworkTitle, "Background artwork",
                "The name to look this game up by on SteamGridDB, for when the background shows the wrong game. Leave empty to go back to the game's own title.",
                Artwork.SearchTitle(tile.Game));
        }
    }

    private void BeginEdit(MenuEditor editor, string title, string hint, string? text)
    {
        if (_menu is null)
        {
            return;
        }

        _editing = editor;
        _menu.Visibility = Visibility.Collapsed;
        TextEditor.Visibility = Visibility.Visible;

        EditorTitle.Text = title;
        EditorHint.Text = hint;
        EditorBox.Text = text ?? string.Empty;
        EditorBox.CaretIndex = EditorBox.Text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EditorBox.Focus());
    }

    private void OnSaveEdit(object sender, RoutedEventArgs e) => SaveEdit();

    private void OnCancelEdit(object sender, RoutedEventArgs e) => ShowMenuItems();

    private void SaveEdit()
    {
        if (_editing is not { } editor)
        {
            return;
        }

        var text = EditorBox.Text.Trim();
        if (editor == MenuEditor.ArtworkKey)
        {
            // Back to the settings list, with the focused game's artwork looked up under the new key.
            LibrarySettings.ArtworkKey = text;
            RefreshSettings();
            ShowMenuItems();
            ReloadBackdrop();
            return;
        }

        if (_menuTile is not { } tile)
        {
            return;
        }

        if (editor == MenuEditor.Arguments)
        {
            var saved = GameCatalog.SaveCustomArguments(tile.Game, text);
            CloseMenu();

            StatusText.Text = !saved ? $"The launch arguments for {tile.Title} could not be saved."
                : text.Length == 0 ? $"Launch arguments cleared for {tile.Title}."
                : $"Launch arguments saved for {tile.Title}.";
        }
        else
        {
            var saved = Artwork.SaveSearchTitle(tile.Game, text);

            // Closing the menu puts focus back on the tile, which loads its background again.
            _backdropKey = null;
            CloseMenu();

            StatusText.Text = !saved ? $"The artwork title for {tile.Title} could not be saved."
                : text.Length == 0 ? $"Looking up artwork for {tile.Title} by its own title."
                : $"Looking up artwork for {tile.Title} as \"{text}\".";
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_menuTile?.Game.InstallDirectory is not { } folder)
        {
            return;
        }

        CloseMenu();
        try
        {
            // The trailing separator makes sure a folder opens, never a same-named program beside it.
            Process.Start(new ProcessStartInfo(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar)
            {
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"The install folder could not be opened: {ex.Message}";
        }
    }

    private void OnShowProperties(object sender, RoutedEventArgs e)
    {
        // The row's detail line is the exe or folder that OpenMenu found.
        if (_menuTile is null || PropertiesItem.Tag is not string target)
        {
            return;
        }

        CloseMenu();
        if (!Native.ShowProperties(target))
        {
            StatusText.Text = "The properties window could not be opened.";
        }
    }

    private void UpdateTopBar()
    {
        ClockText.Text = DateTime.Now.ToString("t");
        _ = UpdateStatusAsync();
    }

    private async Task UpdateStatusAsync()
    {
        if (!_showStatus)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            WifiBars.Visibility = Visibility.Collapsed;
            return;
        }

        // Off the UI thread: the Wi-Fi reading is a call into the WLAN service.
        var status = await Task.Run(SystemStatus.Read);
        if (!_showStatus)
        {
            // Switched off while the reading was on its way.
            return;
        }

        BatteryPanel.Visibility = status.BatteryPercent is null ? Visibility.Collapsed : Visibility.Visible;
        if (status.BatteryPercent is { } percent)
        {
            BatteryText.Text = $"{percent}%";
            BatteryFill.Width = BatteryFillWidth * percent / 100;
            BatteryFill.Fill = percent <= LowBatteryPercent && !status.PluggedIn ? LowBattery : NormalBattery;
            ChargingBolt.Visibility = status.PluggedIn ? Visibility.Visible : Visibility.Collapsed;
        }

        WifiBars.Visibility = status.WifiQuality is null ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < WifiBars.Children.Count; i++)
        {
            WifiBars.Children[i].Opacity = i < status.WifiBars ? 0.85 : 0.25;
        }
    }

    private static Brush FrozenBrush(string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brush;
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
