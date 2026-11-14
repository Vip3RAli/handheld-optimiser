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
        Assert.NotEmpty(list!.Games);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.All(list.Games, game =>
        {
            Assert.NotNull(game.Device);
            Assert.NotEmpty(game.Settings);
            Assert.NotEqual(game.ApplyTo(GameProfile.None), GameProfile.None);

            if (game.Device!.Power is { } power)
            {
                Assert.True(Enum.TryParse<PowerMode>(power, ignoreCase: true, out var mode) && Enum.IsDefined(mode),
                    $"{game.Title} has unknown power mode '{power}'.");
            }

            if (game.Device.Resolution is { } resolution)
            {
                Assert.NotNull(Resolution.Parse(resolution));
            }

            if (game.Device.Fps is { } fps)
            {
                Assert.Contains(fps, AutoTdp.FrameRates);
            }

            foreach (var key in game.Keys)
            {
                Assert.True(seenKeys.Add(key), $"Duplicate key '{key}' on {game.Title}.");
            }

            if (game.FromGuide)
            {
                Assert.NotNull(game.Source);
                Assert.False(string.IsNullOrWhiteSpace(game.Source!.Name), $"{game.Title} guide is missing source name.");
                Assert.True(Uri.TryCreate(game.Source.Url, UriKind.Absolute, out _), $"{game.Title} guide has invalid source URL.");
                Assert.False(string.IsNullOrWhiteSpace(game.Checked), $"{game.Title} guide is missing checked date.");
            }

            AssertNoTypographicDash(game.Title);
            AssertNoTypographicDash(game.Target);
            AssertNoTypographicDash(game.Note);
        });
    }

    private static void AssertNoTypographicDash(string? text)
    {
        if (text is null)
        {
            return;
        }

        Assert.DoesNotContain('\u2013', text);
        Assert.DoesNotContain('\u2014', text);
    }

    [Fact]
    public void For_MatchesByKeyThenByName()
    {
        var list = GameSettingsDb.ReadBuiltIn()!;

        Assert.Equal("Fallout 4", list.For(Steam("377160", "Something else"))?.Title);
        Assert.Equal("Baldur's Gate 3", list.For(Steam("1", "BALDUR’S GATE 3"))?.Title);
        Assert.Equal("Cyberpunk 2077", list.For(new Game("gog:999", "Cyberpunk 2077", GameStore.Gog, null, null, "x"))?.Title);
        Assert.Equal("Diablo IV", list.For(Steam("2344520", "Diablo IV"))?.Title);
        Assert.Equal("Slay the Spire 2", list.For(Steam("2868840", "Slay the Spire 2"))?.Title);
        Assert.Equal("Balatro", list.For(Steam("2379780", "Balatro"))?.Title);
        Assert.Equal("Marvel's Spider-Man: Miles Morales", list.For(Steam("1817190", "Marvel’s Spider-Man: Miles Morales"))?.Title);
        Assert.Equal("Marvel's Spider-Man 2", list.For(Steam("2651280", "Marvel's Spider-Man 2"))?.Title);
        Assert.Equal("The Elder Scrolls V: Skyrim Special Edition", list.For(new Game("gog:1", "The Elder Scrolls V: Skyrim Anniversary Edition", GameStore.Gog, null, null, "x"))?.Title);
        Assert.Equal("Stardew Valley", list.For(new Game("gog:2", "Stardew Valley", GameStore.Gog, null, null, "x"))?.Title);
        Assert.Equal("Overwatch", list.For(new Game("battlenet:pro", "Overwatch 2", GameStore.BattleNet, null, null, "x"))?.Title);
        Assert.Equal("Diablo II: Resurrected", list.For(Steam("2536520", "Diablo II: Resurrected \u2013 Infernal Edition"))?.Title);
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
    public void ApplyTo_LeavesOutASizeOrRateTheScreenDoesNotOffer()
    {
        var list = GameSettingsDb.Parse("""
            { "updated": "2026-01-01", "games": [
              { "title": "Test", "device": { "resolution": "1920x1080", "refresh": 60 } }
            ] }
            """);

        var game = Assert.Single(list!.Games);
        var mine = new GameProfile(null, 120, null, Resolution: new Resolution(1280, 800));

        var onASmallScreen = game.ApplyTo(mine, [new Resolution(1280, 800)], [60, 120]);
        Assert.Equal(new Resolution(1280, 800), onASmallScreen.Resolution);
        Assert.Equal(60, onASmallScreen.RefreshRate);

        var onAFastOnlyScreen = game.ApplyTo(mine, [new Resolution(1920, 1080)], [120, 144]);
        Assert.Equal(new Resolution(1920, 1080), onAFastOnlyScreen.Resolution);
        Assert.Equal(120, onAFastOnlyScreen.RefreshRate);
    }

    [Fact]
    public void Parse_RejectsWhatIsNotAList()
    {
        Assert.Null(GameSettingsDb.Parse("not json"));
        Assert.Null(GameSettingsDb.Parse("{}"));
    }
}
