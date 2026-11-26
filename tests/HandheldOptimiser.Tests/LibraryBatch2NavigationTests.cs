using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public sealed class LibraryBatch2NavigationTests
{
    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;

    [Fact]
    public void GamepadInput_StickToDpad_EnforcesHysteresisAndStickyAxisLock()
    {
        // Engaged past StickEngage (10500), stays locked above StickRelease (7800), releases below StickRelease.
        Assert.Equal(DpadRight, GamepadInput.StickToDpad(11000, 0, 0));
        Assert.Equal(DpadRight, GamepadInput.StickToDpad(8500, 0, DpadRight));
        Assert.Equal(0, GamepadInput.StickToDpad(7000, 0, DpadRight));

        // Slight drift slightly past 45 degrees keeps horizontal axis locked rather than misfiring Down.
        Assert.Equal(DpadRight, GamepadInput.StickToDpad(14000, -16000, DpadRight));

        // Clear steep perpendicular push (> 1.35x) breaks the sticky lock to switch axis.
        Assert.Equal(DpadDown, GamepadInput.StickToDpad(9000, -18000, DpadRight));
    }

    [Theory]
    [InlineData(0, 110)]
    [InlineData(799, 110)]
    [InlineData(800, 65)]
    [InlineData(1799, 65)]
    [InlineData(1800, 40)]
    public void GamepadInput_RepeatIntervalFor_AcceleratesWhileHeld(int heldMs, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), GamepadInput.RepeatIntervalFor(TimeSpan.FromMilliseconds(heldMs)));

    [Fact]
    public void LibraryState_SwitchFilter_RemembersFocusPerTab()
    {
        var state = new LibraryState { Filter = TileFilter.All };
        state.FocusedKey = "steam:100";

        state.SwitchFilter(TileFilter.AppsTab);
        Assert.Null(state.FocusedKey);
        state.FocusedKey = "other:discord";

        state.SwitchFilter(TileFilter.All);
        Assert.Equal("steam:100", state.FocusedKey);

        state.SwitchFilter(TileFilter.AppsTab);
        Assert.Equal("other:discord", state.FocusedKey);
    }

    [Fact]
    public void TileFilter_CollectionsStayVisibleWithoutStoreTabsAndIncludeUninstalledGames()
    {
        var backlog = new GameTile
        {
            Game = new Game("gog:1", "Uninstalled Backlog", GameStore.Gog, null, null, "x", Installed: false),
            Background = Brushes.Black,
            IsFavourite = true,
            Tags = ["Backlog"]
        };

        var backlogFilter = TileFilter.ForTag("Backlog");
        Assert.True(backlogFilter.Shows(backlog));
        Assert.True(TileFilter.Starred.Shows(backlog));

        var options = TileFilter.Options([backlog], storeTabs: false);
        Assert.Contains(TileFilter.Starred, options);
        Assert.Contains(backlogFilter, options);
    }

    [Fact]
    public void Batch2XamlAndCode_SatisfyNavigationSearchAndMenuContracts()
    {
        var lib = Library();
        var menuXaml = File.ReadAllText(Path.Combine(lib, "MenuView.xaml"));
        var topBarXaml = File.ReadAllText(Path.Combine(lib, "TopBarView.xaml"));
        var searchCode = File.ReadAllText(Path.Combine(lib, "SearchPresenter.cs"));
        var ownedMenuCode = File.ReadAllText(Path.Combine(lib, "OwnedGamesMenu.cs"));
        var actionsMenuCode = File.ReadAllText(Path.Combine(lib, "GameActionsMenu.cs"));

        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", menuXaml);
        Assert.Contains("x:Name=\"MenuBoxAbout\"", menuXaml);
        Assert.Contains("x:Name=\"MenuCloseButton\"", menuXaml);
        Assert.Contains("x:Name=\"HiddenGamesItems\"", menuXaml);

        Assert.Contains("x:Name=\"SearchClearButton\"", topBarXaml);
        Assert.DoesNotContain("!_state.Filter.NotInstalled", searchCode);
        Assert.Contains("OpenFolderDialog", ownedMenuCode);
        Assert.Contains("Press again to remove", actionsMenuCode);
    }

    private static string Library([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "HandheldOptimiser.HomeLauncher", "Library"));
}
