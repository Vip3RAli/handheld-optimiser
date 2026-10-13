using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// A grid of tiles, one of which can be open: its detail is one more cell in the list, placed straight
/// after the row the open tile is in, where the panel gives it a row to itself.
/// </summary>
public sealed class TileCellsTests
{
    private sealed class Tile : ITile
    {
        public bool IsOpen { get; set; }
    }

    private static List<Tile> Tiles(int count) => [.. Enumerable.Range(0, count).Select(_ => new Tile())];

    [Theory]
    [InlineData(0, 7, 3, 3)]
    [InlineData(2, 7, 3, 3)]
    [InlineData(3, 7, 3, 6)]
    [InlineData(6, 7, 3, 7)]
    [InlineData(4, 5, 3, 5)]
    [InlineData(1, 2, 4, 2)]
    public void The_detail_goes_after_its_row(int open, int count, int columns, int expected) =>
        Assert.Equal(expected, TileRows.DetailIndex(open, count, columns));

    [Fact]
    public void A_spanning_child_takes_a_row_to_itself()
    {
        (int, int, int)[] expected = [(0, 0, 1), (0, 1, 1), (0, 2, 1), (1, 0, 3), (2, 0, 1)];

        Assert.Equal(expected, TileRows.Place([false, false, false, true, false], 3));
    }

    [Fact]
    public void A_spanning_child_ends_a_row_that_is_not_full()
    {
        (int, int, int)[] expected = [(0, 0, 1), (0, 1, 1), (1, 0, 3), (2, 0, 1)];

        Assert.Equal(expected, TileRows.Place([false, false, true, false], 3));
    }

    [Fact]
    public void Opening_a_tile_puts_its_detail_after_its_row()
    {
        var tiles = Tiles(7);
        var cells = new TileCells(3);
        cells.Reset(tiles);

        cells.Toggle(tiles[1]);

        Assert.Equal(8, cells.Cells.Count);
        Assert.Equal(new TileDetail(tiles[1]), cells.Cells[3]);
        Assert.Same(tiles[1], cells.Open);
        Assert.True(tiles[1].IsOpen);
    }

    [Fact]
    public void Pressing_the_open_tile_closes_it()
    {
        var tiles = Tiles(7);
        var cells = new TileCells(3);
        cells.Reset(tiles);

        cells.Toggle(tiles[1]);
        cells.Toggle(tiles[1]);

        Assert.Equal(tiles, cells.Cells);
        Assert.Null(cells.Open);
        Assert.False(tiles[1].IsOpen);
    }

    [Fact]
    public void Opening_another_tile_moves_the_detail()
    {
        var tiles = Tiles(7);
        var cells = new TileCells(3);
        cells.Reset(tiles);

        cells.Toggle(tiles[1]);
        cells.Toggle(tiles[4]);

        Assert.Equal([new TileDetail(tiles[4])], cells.Cells.OfType<TileDetail>());
        Assert.Equal(new TileDetail(tiles[4]), cells.Cells[6]);
        Assert.False(tiles[1].IsOpen);
        Assert.True(tiles[4].IsOpen);
    }

    /// <summary>A list read again while a tile is open: the detail must not stay, pointing at a row that is gone.</summary>
    [Fact]
    public void Reset_closes_the_open_detail()
    {
        var tiles = Tiles(4);
        var cells = new TileCells(3);
        cells.Reset(tiles);
        cells.Toggle(tiles[0]);

        var fresh = Tiles(5);
        cells.Reset(fresh);

        Assert.Equal(fresh, cells.Cells);
        Assert.Null(cells.Open);
        Assert.False(tiles[0].IsOpen);
    }

    [Fact]
    public void An_empty_list_has_no_cells()
    {
        var cells = new TileCells(3);
        cells.Reset([]);

        cells.Toggle(new Tile());

        Assert.Empty(cells.Cells);
        Assert.Null(cells.Open);
    }

    [Fact]
    public void Opening_in_one_grid_closes_the_other()
    {
        var page = new OpenTile();
        var first = new TileCells(3, page);
        var second = new TileCells(3, page);
        var firstTiles = Tiles(3);
        var secondTiles = Tiles(3);
        first.Reset(firstTiles);
        second.Reset(secondTiles);

        first.Toggle(firstTiles[0]);
        second.Toggle(secondTiles[2]);

        Assert.Null(first.Open);
        Assert.Equal(firstTiles, first.Cells);
        Assert.Same(secondTiles[2], second.Open);
    }
}
