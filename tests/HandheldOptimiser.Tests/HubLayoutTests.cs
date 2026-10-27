using HandheldOptimiser.Models;
using HandheldOptimiser.TweakDefinitions;
using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The map of the app: which goals the Dashboard shows, and which tweaks sit under which heading. A tweak
/// whose category belongs to no goal would be in the app and on no page, so that is what is checked here.
/// </summary>
public sealed class HubLayoutTests
{
    [Fact]
    public void The_goals_are_the_eight_cards_in_order() => Assert.Equal(
        ["Performance", "Battery & sleep", "Clean up", "Apps", "Full screen", "Maintenance & backup", "Health", "Updates"],
        HubLayout.Goals.Select(g => g.Title));

    [Fact]
    public void GPU_memory_comes_first_on_the_Performance_page()
    {
        var first = HubLayout.Goals.Single(g => g.Title == "Performance").Sections[0];

        Assert.Equal(SectionKind.GpuMemory, first.Kind);
        Assert.Equal("GPU memory", first.Heading);
    }

    [Fact]
    public void Every_category_belongs_to_exactly_one_goal()
    {
        foreach (var category in Enum.GetValues<TweakCategory>())
        {
            Assert.Single(HubLayout.Goals, g => g.Sections.Count(s => s.Category == category) == 1);
        }
    }

    [Theory]
    [InlineData("Performance", 11, 8)]
    [InlineData("Battery & sleep", 6, 0)]
    [InlineData("Clean up", 22, 14)]
    [InlineData("Full screen", 11, 6)]
    [InlineData("Maintenance & backup", 5, 1)]
    [InlineData("Apps", 0, 0)]
    public void A_goal_holds_the_tweaks_the_map_gives_it(string title, int tweaks, int recommended)
    {
        var found = HubLayout.TweaksOf(HubLayout.Goals.Single(g => g.Title == title), TweakCatalog.All);

        Assert.Equal(tweaks, found.Count);
        Assert.Equal(recommended, found.Count(t => t.IncludeInOneClick));
    }

    [Theory]
    [InlineData("Turn off power throttling", TweakCategory.System)]
    [InlineData("Remove Windows startup delay", TweakCategory.System)]
    [InlineData("Turn on hardware-accelerated GPU scheduling", TweakCategory.Games)]
    [InlineData("Turn off update sharing with other PCs", TweakCategory.BackgroundActivity)]
    [InlineData("Turn off the Sticky Keys shortcut", TweakCategory.TouchAndDesktop)]
    [InlineData("Turn off Widgets", TweakCategory.AdsAndAssistants)]
    [InlineData("Turn off Windows Error Reporting", TweakCategory.Privacy)]
    [InlineData("Turn off reserved storage", TweakCategory.Storage)]
    [InlineData("Use classic right-click context menu", TweakCategory.TouchAndDesktop)]
    [InlineData("Hide Gallery from File Explorer", TweakCategory.TouchAndDesktop)]
    [InlineData("Turn off pointer acceleration for thumbsticks", TweakCategory.TouchAndDesktop)]
    [InlineData("Turn off Windows Spotlight and lock screen suggestions", TweakCategory.BackgroundActivity)]
    [InlineData("Turn off AMD User Experience telemetry", TweakCategory.Privacy)]
    [InlineData("Uninstall Microsoft Edge (leaves WebView2 intact)", TweakCategory.AdsAndAssistants)]
    [InlineData("Turn off Device Encryption (BitLocker)", TweakCategory.Storage)]
    public void A_tweak_sits_under_its_heading(string name, TweakCategory category) =>
        Assert.Equal(category, TweakCatalog.All.Single(t => t.Name == name).Category);

    [Fact]
    public void The_catalog_holds_all_55_tweaks_once() =>
        Assert.Equal(55, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
}
