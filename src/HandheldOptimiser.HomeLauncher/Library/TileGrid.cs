using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The games' tiles as they are laid out: how many are in a row, and each one's place to give the focus to.</summary>
internal sealed class TileGrid(ItemsControl tiles, LibraryState state, LibraryLayoutManager layouts)
{
    /// <summary>
    /// Tiles in the first row, which is the column count since every tile is the same width. Measured on
    /// the item containers: the focused button is scaled up, which shifts its own position. The row
    /// and the Old School cards are one line of every tile.
    /// </summary>
    public int ColumnCount()
    {
        if (layouts.IsOneLine)
        {
            return Math.Max(1, state.Tiles.Count);
        }

        double? firstTop = null;
        var columns = 0;

        while (columns < state.Tiles.Count && Container(columns) is { } container)
        {
            var top = container.TranslatePoint(default, tiles).Y;
            firstTop ??= top;
            if (Math.Abs(top - firstTop.Value) >= 1)
            {
                break;
            }

            columns++;
        }

        return Math.Max(1, columns);
    }

    /// <summary>Gives a tile the focus, by its place.</summary>
    public void Focus(int index)
    {
        if (Container(index) is { } container)
        {
            Visuals.Child<Button>(container)?.Focus();
        }
    }

    /// <summary>The item container a tile is drawn in, by its place, once it exists.</summary>
    public ContentPresenter? Container(int index) => ItemContainers.At(tiles, index);
}
