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

            // Whether the game's info fits beside the Last played card depends on the width, and how
            // far down the grid sits on a TV or monitor on the height.
            Scroller.SizeChanged += (_, _) => _continue.Update();
            Scroller.SizeChanged += (_, _) => _oldSchool.FitCategories();

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
        ScaledContent.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);

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
