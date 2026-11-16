using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class SameGameTests
{
    private sealed record Game(string Title, string Store, bool Installed = true);

    private static (List<Game> Games, Dictionary<Game, List<Game>> Others) Merge(params Game[] games) =>
        SameGame.Merge(games.ToList(), g => g.Title, g => g.Store, g => g.Installed);

    [Fact]
    public void One_game_on_two_stores_is_one_tile_and_the_first_stays()
    {
        var epic = new Game("Fortnite", "Epic Games");
        var other = new Game("FORTNITE™", "Steam");
        var (games, others) = Merge(epic, new Game("Among Us", "Steam"), other);

        Assert.Equal([epic.Title, "Among Us"], games.Select(g => g.Title));
        Assert.Equal([other], others[epic]);
    }

    [Fact]
    public void Two_of_one_name_on_the_same_store_are_both_kept()
    {
        var (games, others) = Merge(new Game("Doom", "Steam"), new Game("DOOM", "Steam"));
        Assert.Equal(2, games.Count);
        Assert.Empty(others);
    }

    [Fact]
    public void A_third_store_joins_the_same_tile() =>
        Assert.Equal(2, Merge(new Game("Hades", "Steam"), new Game("Hades", "Epic Games"), new Game("Hades", "Xbox")).Others.Single().Value.Count);

    [Fact]
    public void Games_that_are_not_installed_are_not_merged() =>
        Assert.Equal(2, Merge(new Game("Hades", "Steam"), new Game("Hades", "Epic Games", Installed: false)).Games.Count);

    [Theory]
    [InlineData("Baldur's Gate 3", "baldursgate3")]
    [InlineData("FORTNITE™", "fortnite")]
    [InlineData("The Witcher 3: Wild Hunt", "thewitcher3wildhunt")]
    public void Titles_are_compared_by_their_letters_and_digits(string title, string key) =>
        Assert.Equal(key, SameGame.Key(title));
}
