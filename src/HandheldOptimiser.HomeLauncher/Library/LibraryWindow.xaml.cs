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
    private List<GameTile> _tiles = [];
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

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher);
        UpdateClock();

        // Scan on load, not only on activation: Windows can start the home app without giving it focus.
        Loaded += (_, _) => _ = ScanAsync();
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
        _clock.Start();
        UpdateClock();

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
        if (_tiles.Count == 0)
        {
            EmptyText.Text = "Finding your games...";
        }

        try
        {
            var tiles = await Task.Run(() => GameCatalog.Scan().Select(GameTile.Create).ToList());
            _lastScan = DateTime.UtcNow;

            // Rebuilding the tiles resets scrolling, so only do it when something changed.
            if (!tiles.Select(t => t.Game).SequenceEqual(_tiles.Select(t => t.Game)))
            {
                _tiles = tiles;
                Tiles.ItemsSource = _tiles;
                CountText.Text = _tiles.Count == 1 ? "1 game" : $"{_tiles.Count} games";
            }

            EmptyText.Text = _tiles.Count == 0
                ? "No installed games were found in Steam, Epic Games, Battle.net or GOG.\nInstall a game, then press Y to refresh."
                : string.Empty;
        }
        finally
        {
            _scanning = false;
        }

        RestoreFocus();
    }

    private void RestoreFocus()
    {
        if (!IsActive || _tiles.Count == 0)
        {
            return;
        }

        // Wait for the item containers to exist after ItemsSource changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => FocusTile(Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey))));
    }

    /// <summary>
    /// Moves through the grid by row and column. WPF's own directional navigation only moves to a tile
    /// directly in line, so Down did nothing from a column past the end of a shorter last row.
    /// </summary>
    private void OnNavigate(FocusNavigationDirection direction)
    {
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
        switch (action)
        {
            case GamepadAction.Accept when Keyboard.FocusedElement is Button { DataContext: GameTile tile }:
                Launch(tile);
                break;
            case GamepadAction.Refresh:
                _ = ScanAsync();
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
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
        else if (e.Key == Key.F5)
        {
            _ = ScanAsync();
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

    private void OnTileFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: GameTile tile } button)
        {
            _focusedKey = tile.Game.Key;

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

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("t");

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
