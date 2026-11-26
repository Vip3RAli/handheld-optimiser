using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public sealed class LibraryBatch1UxTests
{
    [Theory]
    [InlineData(0, 1280, 720, 1.0)]
    [InlineData(0, 1920, 1080, 1.5)]
    [InlineData(0, 3440, 1440, 2.0)]
    [InlineData(0, 3840, 2160, 3.0)]
    [InlineData(125, 3440, 1440, 1.25)]
    public void Displays_Scale_RespectsHeightOnUltrawide(int size, double width, double height, double expected) =>
        Assert.Equal(expected, Displays.Scale(size, width, height), 3);

    [Fact]
    public void DealTile_HasCut_HidesZeroCutAndShowsDiscountsOrGiveaways()
    {
        var fullPrice = new DealTile(new Deal("1", "Full Price", "Steam", "$19.99", "$19.99", 0, false, false, "https://example.com", null, null));
        var discounted = new DealTile(new Deal("2", "Half Off", "Steam", "$9.99", "$19.99", 50, false, false, "https://example.com", null, null));
        var giveaway = new DealTile(new Deal("3", "Free Game", "Epic", "$0.00", "$19.99", 100, false, false, "https://example.com", null, null, "Free this week"));

        Assert.False(fullPrice.HasCut);
        Assert.Equal(string.Empty, fullPrice.Cut);
        Assert.True(discounted.HasCut);
        Assert.Equal("-50%", discounted.Cut);
        Assert.True(giveaway.HasCut);
        Assert.Equal("Free", giveaway.Cut);
    }

    [Fact]
    public void GameTile_SessionState_TracksRunningPausedAndPropertyNotifications()
    {
        var tile = new GameTile
        {
            Game = new Game("steam:123", "Sample Game", GameStore.Steam, null, null, "steam://rungameid/123"),
            Background = Brushes.Black
        };

        var changed = new List<string>();
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changed.Add(e.PropertyName);
            }
        };

        Assert.Equal(GameSessionState.None, tile.SessionState);
        Assert.False(tile.IsSessionActive);
        Assert.Null(tile.SessionBadge);

        tile.SessionState = GameSessionState.Running;
        Assert.True(tile.IsSessionActive);
        Assert.False(tile.IsSessionPaused);
        Assert.Equal("Running", tile.SessionBadge);
        Assert.Contains(nameof(GameTile.SessionBadge), changed);

        tile.SessionState = GameSessionState.Paused;
        Assert.True(tile.IsSessionActive);
        Assert.True(tile.IsSessionPaused);
        Assert.Equal("Paused", tile.SessionBadge);

        tile.SessionState = GameSessionState.None;
        Assert.False(tile.IsSessionActive);
        Assert.False(tile.IsSessionPaused);
        Assert.Null(tile.SessionBadge);
    }

    [Fact]
    public void LibraryXamlAndNavigation_SatisfyBatch1Contracts()
    {
        var lib = Library();
        var menuXaml = File.ReadAllText(Path.Combine(lib, "MenuView.xaml"));
        var signInXaml = File.ReadAllText(Path.Combine(lib, "SignInView.xaml"));
        var topBarXaml = File.ReadAllText(Path.Combine(lib, "TopBarView.xaml"));
        var windowXaml = File.ReadAllText(Path.Combine(lib, "LibraryWindow.xaml"));
        var coordinator = File.ReadAllText(Path.Combine(lib, "LibraryNavigationCoordinator.cs"));

        Assert.DoesNotContain("Game Mode &amp; Optimisation", menuXaml);
        Assert.Contains("Back to Background Programs.", menuXaml);
        Assert.Contains("Back to Home Screen.", menuXaml);

        Assert.DoesNotContain(" Width=\"940\"", signInXaml);
        Assert.Contains("MaxWidth=\"940\"", signInXaml);

        Assert.DoesNotContain("<ColumnDefinition Width=\"*\" />\r\n            <ColumnDefinition Width=\"Auto\" />\r\n            <ColumnDefinition Width=\"*\" />", topBarXaml);
        Assert.DoesNotContain("_options.QuickActions", coordinator);

        Assert.Contains("x:Name=\"HintBar\"", windowXaml);
        Assert.Contains("x:Name=\"NoticePanel\" Grid.Row=\"2\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Top\"", windowXaml);
    }

    private static string Library([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "HandheldOptimiser.HomeLauncher", "Library"));
}
