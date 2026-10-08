using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Starring games so they come first, and hiding the ones the player never wants to see.</summary>
public partial class LibraryWindow
{
    /// <summary>Puts the stored stars and hidden marks on the tiles.</summary>
    /// <returns>Whether any tile changed.</returns>
    private static bool MarkTiles(IEnumerable<GameTile> tiles)
    {
        var favourites = GameCatalog.Favourites();
        var hidden = GameCatalog.Hidden();
        var changed = false;

        foreach (var tile in tiles)
        {
            var favourite = favourites.Contains(tile.Game.Key);
            var isHidden = hidden.Contains(tile.Game.Key);
            changed |= tile.IsFavourite != favourite || tile.IsHidden != isHidden;
            tile.IsFavourite = favourite;
            tile.IsHidden = isHidden;
        }

        return changed;
    }

    private void OnToggleFavourite(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile)
        {
            return;
        }

        var favourite = !tile.IsFavourite;
        CloseMenu();

        if (!GameCatalog.SetFavourite(tile.Game, favourite))
        {
            StatusText.Text = $"{tile.Title} could not be saved as a favourite.";
            return;
        }

        tile.IsFavourite = favourite;
        ApplyFilter();
        RestoreFocus();
        StatusText.Text = favourite ? $"{tile.Title} added to favourites." : $"{tile.Title} removed from favourites.";
    }

    /// <summary>Hides or shows a game, or takes a program the player added off the library altogether.</summary>
    private void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile)
        {
            return;
        }

        // The focus moves on to the next game rather than jumping back to the first.
        var leaving = AddedPrograms.IsAdded(tile.Game) || (!tile.IsHidden && !_showHidden);
        if (leaving && _tiles.IndexOf(tile) is var index and >= 0)
        {
            var next = index + 1 < _tiles.Count ? index + 1 : index - 1;
            _focusedKey = next >= 0 ? _tiles[next].Game.Key : null;
        }

        CloseMenu();

        if (AddedPrograms.IsAdded(tile.Game))
        {
            if (!AddedPrograms.Remove(tile.Game))
            {
                StatusText.Text = $"{tile.Title} could not be removed.";
                return;
            }

            _allTiles = _allTiles.Where(t => t != tile).ToList();
            ApplyFilter();
            RestoreFocus();
            StatusText.Text = $"{tile.Title} removed from the library.";
            return;
        }

        var hidden = !tile.IsHidden;
        if (!GameCatalog.SetHidden(tile.Game, hidden))
        {
            StatusText.Text = $"{tile.Title} could not be {(hidden ? "hidden" : "shown")}.";
            return;
        }

        tile.IsHidden = hidden;
        ApplyFilter();
        RestoreFocus();
        StatusText.Text = !hidden ? $"{tile.Title} is back in the library."
            : _showHidden ? $"{tile.Title} is hidden. It shows faded while Show hidden games is on."
            : $"{tile.Title} is hidden. Show hidden games in Settings, Display brings it back.";
    }
}
