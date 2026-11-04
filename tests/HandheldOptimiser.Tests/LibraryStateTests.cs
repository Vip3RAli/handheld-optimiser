using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>The library's tabs and the state every part of it reads.</summary>
public sealed class LibraryStateTests
{
    private static GameTile Tile(string key, GameStore store = GameStore.Steam, bool installed = true, bool app = false,
        bool favourite = false, bool hidden = false)
    {
        var tile = GameTile.Create(new Game($"test:{key}", key, store, null, null, "x", Installed: installed, IsApp: app));
        tile.IsFavourite = favourite;
        tile.IsHidden = hidden;
        return tile;
    }

    [Fact]
    public void Games_and_Apps_are_always_there_and_Not_installed_only_with_such_games()
    {
        Assert.Equal([TileFilter.All, TileFilter.AppsTab], TileFilter.Options([Tile("a")], storeTabs: true));
        Assert.Equal([TileFilter.All, TileFilter.AppsTab, TileFilter.Uninstalled],
            TileFilter.Options([Tile("a"), Tile("b", installed: false)], storeTabs: false));
    }

    [Fact]
    public void Store_tabs_need_the_setting_and_more_than_one_store()
    {
        GameTile[] tiles = [Tile("a"), Tile("b", GameStore.Epic), Tile("c", GameStore.Epic, favourite: true)];
        Assert.Equal([TileFilter.All, TileFilter.AppsTab], TileFilter.Options(tiles, storeTabs: false));
        Assert.Equal(
            [TileFilter.All, TileFilter.AppsTab, TileFilter.Starred, new TileFilter(GameStore.Steam, false), new TileFilter(GameStore.Epic, false)],
            TileFilter.Options(tiles, storeTabs: true));

        Assert.Equal([TileFilter.All, TileFilter.AppsTab], TileFilter.Options([Tile("a"), Tile("b")], storeTabs: true));
    }

    [Fact]
    public void An_app_is_only_on_the_Apps_tab()
    {
        var app = Tile("chrome", GameStore.Other, app: true);
        Assert.True(TileFilter.AppsTab.Shows(app));
        Assert.False(TileFilter.All.Shows(app));
        Assert.False(TileFilter.Uninstalled.Shows(app));
        Assert.Equal("Ready to Install", TileFilter.Uninstalled.Name);
        Assert.Equal("Apps", TileFilter.AppsTab.Name);
        Assert.Equal("Games", TileFilter.All.Name);

        // An app that is not installed is no reason for a Ready to install tab.
        Assert.DoesNotContain(TileFilter.Uninstalled, TileFilter.Options([Tile("a"), app], storeTabs: false));
    }

    [Fact]
    public void A_store_tab_shows_that_stores_installed_games()
    {
        var steam = new TileFilter(GameStore.Steam, false);
        Assert.True(steam.Shows(Tile("a")));
        Assert.False(steam.Shows(Tile("b", GameStore.Epic)));
        Assert.False(steam.Shows(Tile("c", installed: false)));
        Assert.Equal("Steam", steam.Name);
    }

    [Fact]
    public void Hidden_games_are_left_out_unless_shown()
    {
        var state = new LibraryState { AllTiles = [Tile("a"), Tile("b", hidden: true)] };
        Assert.Single(state.Shown(showHidden: false));
        Assert.Equal(2, state.Shown(showHidden: true).Count());
    }

    [Fact]
    public void The_focused_tile_is_found_by_its_key()
    {
        var a = Tile("a");
        var b = Tile("b");
        var state = new LibraryState { AllTiles = [a, b], Tiles = [a, b], FocusedKey = b.Game.Key };
        Assert.Equal(1, state.FocusedIndex);
        Assert.Same(b, state.FocusedTile);

        state.FocusedKey = "test:gone";
        Assert.Equal(-1, state.FocusedIndex);
        Assert.Null(state.FocusedTile);
    }

    [Fact]
    public void A_tile_can_be_replaced_or_removed_without_touching_the_others()
    {
        var a = Tile("a");
        var b = Tile("b");
        var c = Tile("c");
        var state = new LibraryState { AllTiles = [a, b] };

        state.Replace(a, c);
        Assert.Equal([c, b], state.AllTiles);

        state.Remove(b);
        Assert.Equal([c], state.AllTiles);
    }

    [Fact]
    public void The_tiles_sort_by_last_played_most_played_or_name()
    {
        var a = Tile("Alpha");
        var b = Tile("bravo");
        var c = Tile("Charlie");
        var now = DateTimeOffset.UtcNow;
        var state = new LibraryState
        {
            Stats = new()
            {
                [a.Game.Key] = new PlayStats(TimeSpan.FromHours(1), now.AddDays(-3)),
                [b.Game.Key] = new PlayStats(TimeSpan.FromHours(9), now.AddDays(-1)),
                [c.Game.Key] = new PlayStats(TimeSpan.FromHours(4), null)
            }
        };

        Assert.Equal([b, a, c], state.Sorted([a, b, c], SortOrder.RecentlyPlayed));
        Assert.Equal([b, c, a], state.Sorted([a, b, c], SortOrder.MostPlayed));
        Assert.Equal([a, b, c], state.Sorted([c, b, a], SortOrder.Name));
    }

    [Fact]
    public void Play_times_that_say_the_same_are_the_same()
    {
        var when = DateTimeOffset.UtcNow;
        Dictionary<string, PlayStats> one = new() { ["x"] = new PlayStats(TimeSpan.FromMinutes(5), when) };
        Dictionary<string, PlayStats> same = new() { ["x"] = new PlayStats(TimeSpan.FromMinutes(5), when) };
        Dictionary<string, PlayStats> longer = new() { ["x"] = new PlayStats(TimeSpan.FromMinutes(6), when) };

        Assert.True(LibraryState.SameStats(one, same));
        Assert.False(LibraryState.SameStats(one, longer));
        Assert.False(LibraryState.SameStats(one, []));
    }

    [Fact]
    public void Nothing_counts_as_scanned_until_the_first_scan_is_in()
    {
        var state = new LibraryState();
        Assert.False(state.Scanned);
        state.LastScan = DateTime.UtcNow;
        Assert.True(state.Scanned);
    }
}
