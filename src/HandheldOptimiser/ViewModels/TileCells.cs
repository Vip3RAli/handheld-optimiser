using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace HandheldOptimiser.ViewModels;

/// <summary>A tile that can be open, so it can draw itself as the one whose detail is showing.</summary>
public interface ITile
{
    bool IsOpen { get; set; }
}

/// <summary>Marks a cell that takes a whole row of a tile grid to itself.</summary>
public interface ISpansRow
{
}

/// <summary>
/// The open tile's detail: what it does, and any warning. It is a cell in the same list as the tiles,
/// which is how it comes to sit directly under the row its tile is in.
/// </summary>
public sealed record TileDetail(object Tile) : ISpansRow;

/// <summary>
/// One per page, shared by every tile grid on it, so that only one tile is open on a page: opening a
/// tile in one grid closes the one open in another.
/// </summary>
public sealed class OpenTile
{
    private TileCells? _current;

    public void Opening(TileCells cells)
    {
        if (!ReferenceEquals(_current, cells))
        {
            _current?.Close();
        }

        _current = cells;
    }
}

/// <summary>
/// The cells of one tile grid: its tiles in order and, while one of them is open, that tile's detail.
/// A tile shows its name and its switch only, which is what lets a whole goal fit on a screen or two.
/// Pressing a tile opens the rest beneath it, and pressing it again closes it.
/// </summary>
public sealed class TileCells
{
    private readonly OpenTile? _page;
    private List<object> _tiles = [];

    public TileCells(int columns, OpenTile? page = null)
    {
        Columns = columns;
        _page = page;
        ToggleCommand = new RelayCommand<object>(tile =>
        {
            if (tile is not null)
            {
                Toggle(tile);
            }
        });
    }

    /// <summary>How many tiles sit side by side. The panel that draws the cells is given the same number.</summary>
    public int Columns { get; }

    /// <summary>The tiles, plus at most one <see cref="TileDetail"/>.</summary>
    public ObservableCollection<object> Cells { get; } = [];

    /// <summary>The tile whose detail is showing, if any.</summary>
    public object? Open { get; private set; }

    /// <summary>A tile was pressed, anywhere but on its switch. The tile is the parameter.</summary>
    public IRelayCommand<object> ToggleCommand { get; }

    /// <summary>
    /// Replaces the tiles, with none open. A list that is read again makes new tiles, and a detail
    /// left open would belong to one that is no longer there.
    /// </summary>
    public void Reset(IEnumerable<object> tiles)
    {
        Close();
        _tiles = [.. tiles];

        Cells.Clear();
        foreach (var tile in _tiles)
        {
            Cells.Add(tile);
        }
    }

    /// <summary>Opens the tile, moving the detail to it from any other, or closes it if it was the open one.</summary>
    public void Toggle(object tile)
    {
        var index = _tiles.FindIndex(t => ReferenceEquals(t, tile));
        if (index < 0)
        {
            return;
        }

        var wasOpen = ReferenceEquals(Open, tile);
        Close();

        if (wasOpen)
        {
            return;
        }

        _page?.Opening(this);
        Open = tile;

        if (tile is ITile opened)
        {
            opened.IsOpen = true;
        }

        Cells.Insert(TileRows.DetailIndex(index, _tiles.Count, Columns), new TileDetail(tile));
    }

    public void Close()
    {
        if (Open is null)
        {
            return;
        }

        if (Open is ITile closed)
        {
            closed.IsOpen = false;
        }

        Open = null;

        for (var i = Cells.Count - 1; i >= 0; i--)
        {
            if (Cells[i] is TileDetail)
            {
                Cells.RemoveAt(i);
            }
        }
    }
}

/// <summary>Where things go in a tile grid. Pure, so it can be tested without a window.</summary>
public static class TileRows
{
    /// <summary>
    /// Where the open tile's detail goes in the list of cells: straight after the last tile of the
    /// row the open tile is in, or at the end when that row is the last and not full.
    /// </summary>
    public static int DetailIndex(int openIndex, int count, int columns) =>
        Math.Min(count, (openIndex / columns + 1) * columns);

    /// <summary>
    /// The row, column and column span of each cell, in order. A cell that spans takes a row to
    /// itself, ending the row before it even if that row was not full.
    /// </summary>
    public static IReadOnlyList<(int Row, int Column, int Span)> Place(IReadOnlyList<bool> spansRow, int columns)
    {
        var placed = new List<(int Row, int Column, int Span)>(spansRow.Count);
        var row = 0;
        var column = 0;

        foreach (var spans in spansRow)
        {
            if (spans)
            {
                if (column > 0)
                {
                    row++;
                    column = 0;
                }

                placed.Add((row, 0, columns));
                row++;
                continue;
            }

            placed.Add((row, column, 1));
            column++;

            if (column == columns)
            {
                row++;
                column = 0;
            }
        }

        return placed;
    }
}
