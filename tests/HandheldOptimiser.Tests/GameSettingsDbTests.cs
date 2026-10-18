using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>The recommended game settings list: that the built-in copy reads, and how games are matched to it.</summary>
public sealed class GameSettingsDbTests
{
    private static Game Steam(string appId, string title) =>
        new($"steam:{appId}", title, GameStore.Steam, null, null, $"steam://rungameid/{appId}");

    [Fact]
    public void BuiltInList_ReadsEveryGame()
    {
        var list = GameSettingsDb.ReadBuiltIn();

        Assert.NotNull(list);
        Assert.Equal(9, list!.Games.Count);
        Assert.All(list.Games, game =>
        {
            Assert.NotNull(game.Device);
            Assert.NotEmpty(game.Settings);
            Assert.NotEqual(game.ApplyTo(GameProfile.None), GameProfile.None);
        });
    }

    [Fact]
    public void For_MatchesByKeyThenByName()
    {
        var list = GameSettingsDb.ReadBuiltIn()!;

        Assert.Equal("Fallout 4", list.For(Steam("377160", "Something else"))?.Title);
        Assert.Equal("Baldur's Gate 3", list.For(Steam("1", "BALDUR’S GATE 3"))?.Title);
        Assert.Equal("Cyberpunk 2077", list.For(new Game("gog:999", "Cyberpunk 2077", GameStore.Gog, null, null, "x"))?.Title);
        Assert.Null(list.For(Steam("2", "Some Other Game")));
        Assert.Null(list.For(Steam("377160", "Fallout 4") with { IsApp = true }));
    }

    [Fact]
    public void ApplyTo_KeepsWhatTheRecommendationLeavesOut()
    {
        var list = GameSettingsDb.Parse("""
            { "updated": "2026-01-01", "games": [
              { "title": "Test", "device": { "power": "performance", "fps": 60 }, "settings": [ { "name": "A", "value": "B" } ] },
              { "settings": [] }
            ] }
            """);

        Assert.NotNull(list);
        var game = Assert.Single(list!.Games);
        var mine = new GameProfile(PowerMode.Efficiency, 120, 40, CloseApps: false, new Resolution(1280, 800), AutoTdpFps: 30);
        var applied = game.ApplyTo(mine);

        Assert.Equal(PowerMode.Performance, applied.PowerMode);
        Assert.Equal(60, applied.AutoTdpFps);
        Assert.Equal(120, applied.RefreshRate);
        Assert.Equal(40, applied.Brightness);
        Assert.False(applied.CloseApps);
        Assert.Equal(new Resolution(1280, 800), applied.Resolution);
    }

    [Fact]
    public void Parse_RejectsWhatIsNotAList()
    {
        Assert.Null(GameSettingsDb.Parse("not json"));
        Assert.Null(GameSettingsDb.Parse("{}"));
    }
}
