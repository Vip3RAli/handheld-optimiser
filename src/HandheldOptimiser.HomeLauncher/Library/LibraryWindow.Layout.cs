using System.Windows;
using System.Windows.Controls;
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
    // Tiles in the row layout are this much bigger, as the single row has the screen's height to itself.
    private const double RowTileScale = 1.2;

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

            // How many games fit beside the Continue playing card depends on the width.
            Scroller.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged)
                {
                    UpdateContinue();
                }
            };
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

        var layout = _dockedLayout == DockedLayout.Grid ? LibraryLayout.Grid : LibraryLayout.Row;
        return (layout,Displays.Scale(_dockedSize, ActualWidth));
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
        (_layout, _scale) = WantedLayout();
        ScaledContent.LayoutTransform = _scale == 1 ? Transform.Identity : new ScaleTransform(_scale, _scale);

        var row = _layout == LibraryLayout.Row;
        Tiles.ItemsPanel = (ItemsPanelTemplate)FindResource(row ? "RowPanel" : "GridPanel");
        Tiles.LayoutTransform = row ? new ScaleTransform(RowTileScale, RowTileScale) : Transform.Identity;

        Scroller.VerticalScrollBarVisibility = row ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        Scroller.HorizontalScrollBarVisibility = row ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Scroller.PanningMode = row ? PanningMode.HorizontalOnly : PanningMode.VerticalOnly;
        Scroller.VerticalAlignment = row ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        RowDetails.Visibility = row ? Visibility.Visible : Visibility.Collapsed;

        UpdateContinue();
        UpdateDeals();
        RestoreFocus();
    }

    /// <summary>The focused game's name and details above the row, in the row layout.</summary>
    private void ShowRowDetails(GameTile tile)
    {
        if (_layout != LibraryLayout.Row)
        {
            return;
        }

        RowTitle.Text = tile.Title;
        RowDetail.Text = tile.IsInstalled
            ? GameSubtitle(tile)
            : $"{tile.StoreName}   Not installed. A opens {Game.NameOf(tile.Game.Store)} to install it";
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
        OpenSettingsGroup(TvItems, "Docked mode");
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
        LayoutSetting.Tag = LibrarySettings.Layout == LibraryLayout.Row
            ? "Row: one line of bigger games that scrolls across"
            : "Grid: rows of games that scroll down";
        ContinueSetting.Tag = _continuePlaying
            ? "On: your last game and the few before it, above the grid"
            : "Off";

        var dockedMode = LibrarySettings.DockedMode;
        DockedSetting.Tag = dockedMode
            ? "On: a layout and zoom of its own on a TV or monitor"
            : "Off: the same as on the built-in screen";

        DockedLayoutSetting.IsEnabled = dockedMode;
        DockedLayoutSetting.Tag = LibrarySettings.DockedLayout == DockedLayout.Grid ? "Grid" : "Row";

        DockedSizeSetting.IsEnabled = dockedMode;
        DockedSizeSetting.Tag = LibrarySettings.DockedSize is var size and > 0
            ? $"{size}%"
            : "Fit to the screen: as many games across as on the handheld";
    }
}
