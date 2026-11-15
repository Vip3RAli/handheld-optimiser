using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class HomeOrderTests
{
    private sealed record Game(string Name);

    private static readonly Game Fortnite = new("Fortnite");
    private static readonly Game Asphalt = new("Asphalt");
    private static readonly Game Among = new("Among Us");

    private static List<string> Names(List<Game> games) => games.Select(g => g.Name).ToList();

    [Fact]
    public void The_last_played_goes_first_ahead_of_the_favourites() =>
        Assert.Equal(["Among Us", "Fortnite", "Asphalt"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Among)));

    [Fact]
    public void A_last_played_already_first_is_shown_once() =>
        Assert.Equal(["Fortnite", "Asphalt", "Among Us"], Names(HomeOrder.Arrange([Fortnite, Asphalt, Among], Fortnite)));

    [Fact]
    public void Nothing_played_leaves_the_order() =>
        Assert.Equal(["Fortnite", "Asphalt"], Names(HomeOrder.Arrange([Fortnite, Asphalt], null)));

    [Fact]
    public void A_last_played_not_on_this_tab_leaves_the_order() =>
        Assert.Equal(["Asphalt"], Names(HomeOrder.Arrange([Asphalt], Fortnite)));
}
