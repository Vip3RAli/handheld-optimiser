using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The grid and row layouts, and docked mode: the library's own layout and size while it is on a TV or
/// monitor, switched as the screen is plugged in and out.
/// </summary>
public partial class LibraryWindow
{
    // A game's square icon in the row, in DIPs as LibraryWindow.xaml draws it, and how much bigger the
    // focused one is drawn, as the picked game is on a console's home screen.
    private const double RowTileSize = 150;
    private const double RowFocusScale = 2;

    // The settings the layout was last drawn with, to tell when one has changed.
    private LibraryLayout _layoutSetting = LibrarySettings.Layout;
    private bool _dockedMode = LibrarySettings.DockedMode;
    private DockedLayout _dockedLayout = LibrarySettings.DockedLayout;
    private int _dockedSize = LibrarySettings.DockedSize;

    // Whether the library is on a TV or monitor now, and the layout and size it is drawn at: to begin
    // with, the grid at its own size, as LibraryWindow.xaml draws it.
    private bool _docked;
    private LibraryLayout _layout = LibraryLayout.Grid;
    private double _scale = 1;
    private bool _watchingScreens;

    /// <summary>Starts following screens being plugged in and out, and draws the layout for the screen in use.</summary>
    private void WatchScreens()
    {
        if (!_watchingScreens)
        {
            _watchingScreens = true;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Moving to another screen, as Windows does when the built-in one is switched off, changes both.
            DpiChanged += (_, _) => QueueScreenCheck();
            SizeChanged += (_, _) =>
            {
                QueueScreenCheck();

                // The background is made at the screen's size, which a new resolution changes.
                ReloadBackdrop();
            };

            // Whether the game's info fits beside the Last played card depends on the width, and how
            // far down the grid sits on a TV or monitor on the height.
            Scroller.SizeChanged += (_, _) => UpdateContinue();
            Scroller.SizeChanged += (_, _) => FitCategories();

            // The pictures of the games that are not installed follow what is on screen.
            Scroller.ScrollChanged += OnGridScrolled;
        }

        CheckScreen();
    }

    private void StopWatchingScreens()
    {
        if (_watchingScreens)
        {
            _watchingScreens = false;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
    }

    // Raised on a thread of its own.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(QueueScreenCheck);

    private bool _screenCheckQueued;

    /// <summary>Plugging in a screen raises several of these in a row; they are looked at once, after the last.</summary>
    private void QueueScreenCheck()
    {
        if (_screenCheckQueued)
        {
            return;
        }

        _screenCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _screenCheckQueued = false;
            CheckScreen();
        });
    }

    private void CheckScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var docked = handle != 0 && Displays.IsDocked(handle);
        if (docked != _docked)
        {
            _docked = docked;
            Program.Log(docked ? "The library is on a TV or monitor" : "The library is on the built-in screen");
        }

        if (LayoutChanged())
        {
            ApplyLayout();
        }
    }

    /// <summary>The layout and size the library should be drawn at now.</summary>
    private (LibraryLayout Layout, double Scale) WantedLayout()
    {
        if (!_docked || !_dockedMode)
        {
            return (_layoutSetting, 1);
        }

        var layout = _dockedLayout switch
        {
            DockedLayout.Grid => LibraryLayout.Grid,
            DockedLayout.OldSchool => LibraryLayout.OldSchool,
            _ => LibraryLayout.Row
        };

        return (layout, Displays.Scale(_dockedSize, ActualWidth));
    }

    /// <summary>Whether the settings or the screen ask for a different layout or size than the one on screen.</summary>
    private bool LayoutChanged()
    {
        _layoutSetting = LibrarySettings.Layout;
        _dockedMode = LibrarySettings.DockedMode;
        _dockedLayout = LibrarySettings.DockedLayout;
        _dockedSize = LibrarySettings.DockedSize;

        var (layout, scale) = WantedLayout();
        return layout != _layout || Math.Abs(scale - _scale) > 0.01;
    }

    /// <summary>Draws the library in the layout and at the size the settings and the screen ask for.</summary>
    private void ApplyLayout()
    {
        var scale = _scale;
        (_layout, _scale) = WantedLayout();
        ScaledContent.LayoutTransform = _scale == 1 ? Transform.Identity : new ScaleTransform(_scale, _scale);

        // The cards' artwork is made at the size they are drawn, which has just changed.
        if (Math.Abs(scale - _scale) > 0.01)
        {
            UnloadCardArt();
            QueueCardReset();
        }

        ApplyTilePanel();

        // The tiles are a grid in the open Not installed card as on that tab in the other layouts, so a
        // change between those leaves the panel as it is, and what is around it still has to change.
        RefreshCategories();
        UpdateContinue();
        UpdateDeals();
        RestoreFocus();
    }

    /// <summary>
    /// Whether the games are in one row that scrolls across, as on a console's home screen: the row
    /// layout, but for the Not installed tab, whose hundreds of games are a grid in either layout.
    /// </summary>
    private bool IsRow => _layout == LibraryLayout.Row && !_filter.NotInstalled;

    // Which of the layouts the tiles were last laid out as, to do it only when it changes.
    private LibraryLayout? _panelShown;

    /// <summary>
    /// Lays the tiles out as the grid, the row or the Old School cards, and has the focused tile stand
    /// out as each does.
    /// </summary>
    private void ApplyTilePanel()
    {
        var panel = IsOldSchool ? LibraryLayout.OldSchool : IsRow ? LibraryLayout.Row : LibraryLayout.Grid;
        if (panel == _panelShown)
        {
            return;
        }

        _panelShown = panel;
        var row = panel == LibraryLayout.Row;
        var cards = panel == LibraryLayout.OldSchool;

        Tiles.ItemsPanel = (ItemsPanelTemplate)FindResource(cards ? "OldSchoolPanel" : row ? "RowPanel" : "GridPanel");
        TilesScroller.HorizontalScrollBarVisibility = row ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        TilesScroller.PanningMode = row ? PanningMode.HorizontalFirst : PanningMode.None;
        TilesScroller.ScrollToHorizontalOffset(0);

        Tiles.ItemTemplate = (DataTemplate)FindResource(cards ? "OldSchoolTile" : row ? "RowTile" : "GridTile");

        // The focused icon grows upwards from its bottom edge, so the row needs that much room above it.
        // The cards need room for the focus ring.
        Tiles.Margin = row ? new Thickness(0, (RowTileSize * (RowFocusScale - 1)) + 10, 0, 6)
            : cards ? new Thickness(10, 6, 10, 0)
            : default;

        // The Old School view has its categories in place of the top bar, and its cards at the bottom.
        RefreshCategories();
        UpdateRenderMode();
        if (cards)
        {
            GameCards.Fresh = true;
            QueueCardReset();
        }
        else
        {
            UnloadCardArt();
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
        if (!IsOneLine)
        {
            Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            return;
        }

        _rowWheel += e.Delta;
        var steps = _rowWheel / Mouse.MouseWheelDeltaForOneLine;
        if (steps == 0 || _menu is not null || _tiles.Count == 0)
        {
            return;
        }

        // Down or towards the player is on to the right.
        _rowWheel -= steps * Mouse.MouseWheelDeltaForOneLine;
        var from = Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey));
        FocusTile(Math.Clamp(from - steps, 0, _tiles.Count - 1));
    }

    // ----- Settings: Display -----

    private void OnStepLayout(object sender, RoutedEventArgs e) => StepLayout(1);

    private void StepLayout(int step)
    {
        LibrarySettings.Layout = Step(LibrarySettings.Layout, step);
        RefreshSettings();
        if (LayoutChanged())
        {
            ApplyLayout();
        }
    }

    private void OnToggleContinue(object sender, RoutedEventArgs e)
    {
        _continuePlaying = !_continuePlaying;
        LibrarySettings.ContinuePlaying = _continuePlaying;
        RefreshSettings();
        UpdateContinue();
    }

    // ----- Settings: Docked mode -----

    private void OnOpenTvSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsGroup(TvItems, "Docked mode", LibraryDisplay);
        MenuStore.Text = _docked ? "The library is on a TV or monitor now" : "The library is on the built-in screen now";
    }

    private void OnToggleDocked(object sender, RoutedEventArgs e)
    {
        LibrarySettings.DockedMode = !LibrarySettings.DockedMode;
        AfterDockedSetting();
    }

    private void OnStepDockedLayout(object sender, RoutedEventArgs e) => StepDockedLayout(1);

    private void StepDockedLayout(int step)
    {
        LibrarySettings.DockedLayout = Step(LibrarySettings.DockedLayout, step);
        AfterDockedSetting();
    }

    private void OnStepDockedSize(object sender, RoutedEventArgs e) => StepDockedSize(1);

    private void StepDockedSize(int step)
    {
        var sizes = LibrarySettings.DockedSizes;
        var index = Array.IndexOf(sizes, LibrarySettings.DockedSize);
        LibrarySettings.DockedSize = sizes[(Math.Max(index, 0) + step + sizes.Length) % sizes.Length];
        AfterDockedSetting();
    }

    private void AfterDockedSetting()
    {
        RefreshSettings();
        if (LayoutChanged())
        {
            ApplyLayout();
        }
    }

    /// <summary>Left and right on the layout rows of Display and Docked mode.</summary>
    private bool StepLayoutSetting(Button row, int step)
    {
        if (ReferenceEquals(row, LayoutSetting))
        {
            StepLayout(step);
        }
        else if (ReferenceEquals(row, DockedLayoutSetting))
        {
            StepDockedLayout(step);
        }
        else if (ReferenceEquals(row, DockedSizeSetting))
        {
            StepDockedSize(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void RefreshLayoutSettings()
    {
        LayoutSetting.Tag = LibrarySettings.Layout switch
        {
            LibraryLayout.Row => "Row",
            LibraryLayout.OldSchool => "Old School",
            _ => "Grid"
        };
        ContinueSetting.Tag = OnOff(_continuePlaying);

        ContinueInfoSetting.IsEnabled = _continuePlaying;
        ContinueInfoSetting.Tag = OnOff(_continueInfo);

        StoreDetailsSetting.IsEnabled = _continuePlaying && _continueInfo;
        StoreDetailsSetting.Tag = OnOff(_storeDetails);

        var dockedMode = LibrarySettings.DockedMode;
        DockedSetting.Tag = OnOff(dockedMode);

        DockedLayoutSetting.IsEnabled = dockedMode;
        DockedLayoutSetting.Tag = LibrarySettings.DockedLayout switch
        {
            DockedLayout.Grid => "Grid",
            DockedLayout.OldSchool => "Old School",
            _ => "Row"
        };

        DockedSizeSetting.IsEnabled = dockedMode;
        DockedSizeSetting.Tag = LibrarySettings.DockedSize is var size and > 0
            ? $"{size}%"
            : "Fit to the screen";
    }
}
