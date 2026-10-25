using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Which layout the library is drawn in and at what size: the player's own layout on the built-in screen,
/// and docked mode's layout and size while it is on a TV or monitor, switched as the screen is plugged in
/// and out. It lays the tiles out for the layout too. What else changes with the layout is the window's to
/// do, in its order, so a change is only announced.
/// </summary>
internal sealed class LibraryLayoutManager(Window window, LibraryState state)
{
    // A game's square icon in the row, in DIPs as LibraryResources.xaml draws it, and how much bigger the
    // focused one is drawn, as the picked game is on a console's home screen.
    private const double RowTileSize = 150;
    private const double RowFocusScale = 2;

    // The settings the layout was last drawn with, to tell when one has changed.
    private LibraryLayout _layoutSetting = LibrarySettings.Layout;
    private bool _dockedMode = LibrarySettings.DockedMode;
    private DockedLayout _dockedLayout = LibrarySettings.DockedLayout;
    private int _dockedSize = LibrarySettings.DockedSize;

    private bool _watching;
    private bool _checkQueued;

    // Which of the layouts the tiles were last laid out as, to do it only when it changes.
    private LibraryLayout? _panelShown;

    /// <summary>The screen was plugged in or out, or moved to, and the layout or size it asks for is not the one on screen.</summary>
    public event Action? ScreenChanged;

    /// <summary>The layout on screen: to begin with the grid, as LibraryWindow.xaml draws it.</summary>
    public LibraryLayout Layout { get; private set; } = LibraryLayout.Grid;

    /// <summary>How much bigger than its own size everything is drawn, on a TV or monitor.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Whether the library is on a TV or monitor now.</summary>
    public bool Docked { get; private set; }

    /// <summary>
    /// Whether the games are in one row that scrolls across, as on a console's home screen: the row
    /// layout, but for the Not installed tab, whose hundreds of games are a grid in either layout.
    /// </summary>
    public bool IsRow => Layout == LibraryLayout.Row && !state.Filter.NotInstalled;

    /// <summary>
    /// Whether the games are the Old School cards: that layout, but for the Not installed tab, whose
    /// hundreds of games are a grid in every layout.
    /// </summary>
    public bool IsOldSchool => Layout == LibraryLayout.OldSchool && !state.Filter.NotInstalled;

    /// <summary>Whether the games are in one line across, in the row or as the Old School cards.</summary>
    public bool IsOneLine => IsRow || IsOldSchool;

    /// <summary>Whether the Old School layout's Not installed card is open, with the grid inside it.</summary>
    public bool IsBrowsing => Layout == LibraryLayout.OldSchool && state.Filter.NotInstalled;

    /// <summary>Starts following screens being plugged in and out, and looks at the screen in use.</summary>
    public void Watch()
    {
        if (!_watching)
        {
            _watching = true;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Moving to another screen, as Windows does when the built-in one is switched off, changes both.
            window.DpiChanged += (_, _) => QueueCheck();
            window.SizeChanged += (_, _) => QueueCheck();
        }

        Check();
    }

    public void StopWatching()
    {
        if (_watching)
        {
            _watching = false;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
    }

    // Raised on a thread of its own.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(QueueCheck);

    /// <summary>Plugging in a screen raises several of these in a row; they are looked at once, after the last.</summary>
    private void QueueCheck()
    {
        if (_checkQueued)
        {
            return;
        }

        _checkQueued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _checkQueued = false;
            Check();
        });
    }

    private void Check()
    {
        var handle = new WindowInteropHelper(window).Handle;
        var docked = handle != 0 && Displays.IsDocked(handle);
        if (docked != Docked)
        {
            Docked = docked;
            Program.Log(docked ? "The library is on a TV or monitor" : "The library is on the built-in screen");
        }

        if (LayoutChanged())
        {
            ScreenChanged?.Invoke();
        }
    }

    /// <summary>The layout and size the library should be drawn at now.</summary>
    private (LibraryLayout Layout, double Scale) Wanted()
    {
        if (!Docked || !_dockedMode)
        {
            return (_layoutSetting, 1);
        }

        var layout = _dockedLayout switch
        {
            DockedLayout.Grid => LibraryLayout.Grid,
            DockedLayout.OldSchool => LibraryLayout.OldSchool,
            _ => LibraryLayout.Row
        };

        return (layout, Displays.Scale(_dockedSize, window.ActualWidth));
    }

    /// <summary>Whether the settings or the screen ask for a different layout or size than the one on screen.</summary>
    public bool LayoutChanged()
    {
        _layoutSetting = LibrarySettings.Layout;
        _dockedMode = LibrarySettings.DockedMode;
        _dockedLayout = LibrarySettings.DockedLayout;
        _dockedSize = LibrarySettings.DockedSize;

        var (layout, scale) = Wanted();
        return layout != Layout || Math.Abs(scale - Scale) > 0.01;
    }

    /// <summary>Takes the layout and size the settings and the screen ask for.</summary>
    /// <returns>Whether the size changed, which everything made at the size it is drawn has to follow.</returns>
    public bool Apply()
    {
        var scale = Scale;
        (Layout, Scale) = Wanted();
        return Math.Abs(scale - Scale) > 0.01;
    }

    /// <summary>
    /// Lays the tiles out as the grid, the row or the Old School cards, and has the focused tile stand out
    /// as each does, when that has changed.
    /// </summary>
    /// <returns>Whether it changed.</returns>
    public bool ApplyTilePanel(ItemsControl tiles, ScrollViewer scroller)
    {
        var panel = IsOldSchool ? LibraryLayout.OldSchool : IsRow ? LibraryLayout.Row : LibraryLayout.Grid;
        if (panel == _panelShown)
        {
            return false;
        }

        _panelShown = panel;
        var row = panel == LibraryLayout.Row;
        var cards = panel == LibraryLayout.OldSchool;

        tiles.ItemsPanel = (ItemsPanelTemplate)window.FindResource(cards ? "OldSchoolPanel" : row ? "RowPanel" : "GridPanel");
        scroller.HorizontalScrollBarVisibility = row ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        scroller.PanningMode = row ? PanningMode.HorizontalFirst : PanningMode.None;
        scroller.ScrollToHorizontalOffset(0);

        tiles.ItemTemplate = (DataTemplate)window.FindResource(cards ? "OldSchoolTile" : row ? "RowTile" : "GridTile");

        // The focused icon grows upwards from its bottom edge, so the row needs that much room above it.
        // The cards need room for the focus ring.
        tiles.Margin = row ? new Thickness(0, (RowTileSize * (RowFocusScale - 1)) + 10, 0, 6)
            : cards ? new Thickness(10, 6, 10, 0)
            : default;
        return true;
    }
}
