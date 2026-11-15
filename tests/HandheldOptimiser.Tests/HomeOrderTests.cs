using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class HomeOrderTests
{
    private sealed record Game(string Name, bool Favourite);

    private static readonly Game Fortnite = new("Fortnite", true);
    private static readonly Game Asphalt = new("Asphalt", false);
    private static readonly Game Among = new("Among Us", false);

    private static List<string> Names(List<Game> games) => games.Select(g => g.Name).ToList();

    [Fact]
    public void A_favourite_on_jump_back_in_is_not_among_the_games_too() =>
        Assert.Equal(["Asphalt", "Among Us"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Fortnite, cards: false, g => g.Favourite)));

    [Fact]
    public void A_game_that_is_not_a_favourite_stays_where_it_is() =>
        Assert.Equal(["Fortnite", "Asphalt", "Among Us"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Asphalt, cards: false, g => g.Favourite)));

    [Fact]
    public void In_nxe_the_last_played_is_the_front_card_before_the_favourites() =>
        Assert.Equal(["Among Us", "Fortnite", "Asphalt"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Among, cards: true, g => g.Favourite)));

    [Fact]
    public void In_nxe_a_favourite_last_played_is_shown_once() =>
        Assert.Equal(["Fortnite", "Asphalt", "Among Us"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Fortnite, cards: true, g => g.Favourite)));

    [Fact]
    public void Nothing_played_leaves_the_order() =>
        Assert.Equal(["Fortnite", "Asphalt"], Names(HomeOrder.Arrange([Fortnite, Asphalt], null, cards: true, g => g.Favourite)));

    [Fact]
    public void A_last_played_not_on_this_tab_leaves_the_order() =>
        Assert.Equal(["Asphalt"], Names(HomeOrder.Arrange([Asphalt], Fortnite, cards: false, g => g.Favourite)));
}
