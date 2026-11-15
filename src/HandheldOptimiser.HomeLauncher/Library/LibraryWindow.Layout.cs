using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What follows a change of layout or size, in its order. Which layout it is, and docked mode, are
/// LibraryLayoutManager's.
/// </summary>
public partial class LibraryWindow
{
    private bool _watchingScreens;

    /// <summary>Starts following screens being plugged in and out, and draws the layout for the screen in use.</summary>
    private void WatchScreens()
    {
        if (!_watchingScreens)
        {
            _watchingScreens = true;

            // The background is made at the screen's size, which a new resolution changes.
            SizeChanged += (_, _) => _backdrop.Reload();

            // Whether the game's details below the row have room for its description depends on the width.
            Scroller.SizeChanged += (_, _) => _continue.Update();
            Scroller.SizeChanged += (_, _) => _oldSchool.FitCategories();

            // The row stays in the middle as the deals come and go below it, or the screen changes.
            Scroller.SizeChanged += (_, _) => CentreRow();
            Page.SizeChanged += (_, e) =>
            {
                if (e.HeightChanged)
                {
                    CentreRow();
                }
            };

            // The pictures of the games that are not installed follow what is on screen.
            Scroller.ScrollChanged += _input.OnGridScrolled;
        }

        _layouts.Watch();
    }

    /// <summary>Draws the library in the layout and at the size the settings and the screen ask for.</summary>
    private void ApplyLayout()
    {
        var scaleChanged = _layouts.Apply();
        var scale = _layouts.Scale;

        // Each layout has its own Home Screen settings, so a new one reads them all again.
        var layoutSettingsChanged = LibrarySettings.ShownLayout != _layouts.Layout;
        if (layoutSettingsChanged)
        {
            LibrarySettings.ShownLayout = _layouts.Layout;
            _options.Reload();
            _options.ReloadShowFilter();
            if (_options.ReloadContinue().InfoChanged)
            {
                _continue.ForgetInfo();
            }

            OptionsHint.Visibility = _options.QuickActions ? Visibility.Visible : Visibility.Collapsed;
            _ = _topBar.UpdateStatusAsync();
        }
        ScaledContent.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);

        // The top bar and the hints line up with the games: nearer the sides in the grid and the row, and
        // where the Old School cards start in that layout.
        var narrow = _layouts.Layout != LibraryLayout.OldSchool;
        Resources["TopBarEdges"] = narrow ? new Thickness(24, 14, 24, 8) : new Thickness(48, 14, 48, 8);
        Resources["HintBarEdges"] = narrow ? new Thickness(24, 2, 24, 12) : new Thickness(48, 2, 48, 12);

        // The cards' artwork is made at the size they are drawn, which has just changed.
        if (scaleChanged)
        {
            _artwork.UnloadCardArt();
            _oldSchool.QueueCardReset();
        }

        ApplyTilePanel();

        // The tiles are a grid in the open Not installed card as on that tab in the other layouts, so a
        // change between those leaves the panel as it is, and what is around it still has to change.
        _oldSchool.RefreshCategories();
        _continue.Update();
        UpdateDeals();
        UpdateHeadings();
        if (layoutSettingsChanged)
        {
            // Its own hidden games, order and store tabs.
            ApplyFilter();
        }

        RestoreFocus();
    }

    /// <summary>
    /// Lays the tiles out as the grid, the row or the Old School cards, when that changed, and what goes
    /// with the Old School cards: their categories in place of the top bar, the GPU, and their artwork.
    /// </summary>
    private void ApplyTilePanel()
    {
        if (!_layouts.ApplyTilePanel(Tiles, TilesScroller))
        {
            return;
        }

        _oldSchool.RefreshCategories();
        _oldSchool.UpdateRenderMode();
        _menu.Reshape();
        if (_layouts.IsOldSchool)
        {
            _oldSchool.GameCards.Fresh = true;
            _oldSchool.QueueCardReset();
        }
        else
        {
            _artwork.UnloadCardArt();
        }
    }
}
