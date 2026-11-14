using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The Old School layout's Console space look: how much smaller the stack is drawn to leave room for the
/// floor, and the menu panel.
/// </summary>
public class OldSchoolConsoleSpaceTests
{
    [Fact]
    public void Console_space_draws_the_cards_a_tenth_smaller()
    {
        Assert.True(OldSchoolStack.Fit(3000, console: true) < OldSchoolStack.Fit(3000));
        Assert.Equal(0.9, OldSchoolStack.Fit(900, console: true), 6);
        Assert.Equal(0.9, OldSchoolStack.Fit(double.PositiveInfinity, console: true), 6);
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1280)]
    [InlineData(1500)]
    [InlineData(1650)]
    public void Console_space_cards_are_a_tenth_smaller_than_classic_on_the_same_page(double width) =>
        Assert.Equal(OldSchoolStack.Fit(width) * 0.9, OldSchoolStack.Fit(width, console: true), 6);

    [Theory]
    [InlineData(1204)]
    [InlineData(1500)]
    [InlineData(1844)]
    public void Console_space_spreads_the_stack_to_the_right_edge(double width)
    {
        var spread = OldSchoolStack.Spread(width);
        Assert.InRange(spread, 1e-6, 1);

        // The last card on show ends at the page's right edge, and the front card stays where it was.
        var (scale, left, _) = OldSchoolStack.Place(4, spread);
        Assert.Equal(width, (left + (scale * OldSchoolStack.CardWidth)) * OldSchoolStack.Fit(width, console: true), 6);
        Assert.Equal((1.0, 0.0, 1.0), OldSchoolStack.Place(0, spread));
    }

    [Fact]
    public void Spread_cards_still_tuck_behind_the_one_before()
    {
        for (var depth = 0; depth < 5; depth++)
        {
            var (scale, left, _) = OldSchoolStack.Place(depth, 1);
            var next = OldSchoolStack.Place(depth + 1, 1);
            Assert.True(next.Left < left + (scale * OldSchoolStack.CardWidth));
            Assert.True(next.Scale < scale);
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void The_menu_panel_takes_about_two_thirds_of_the_screen(double width, double height)
    {
        var (panelWidth, panelHeight) = MenuPresenter.PanelSize(width, height);
        Assert.InRange(panelWidth, 760, 1180);
        Assert.InRange(panelHeight, 440, 720);
        Assert.True(panelWidth < width && panelHeight < height);
    }

    [Fact]
    public void The_menu_panel_never_runs_off_a_small_screen()
    {
        var (width, height) = MenuPresenter.PanelSize(640, 400);
        Assert.Equal(608, width);
        Assert.Equal(368, height);
    }

    [Fact]
    public void Every_menu_row_has_an_icon_and_says_what_it_does_without_dashes()
    {
        var xaml = File.ReadAllText(Path.Combine(Library(), "MenuView.xaml"));
        var rows = Regex.Matches(xaml, "<Button x:Name=\"(\\w+)\" Style=\"\\{StaticResource MenuButton\\}\"(?<attributes>[^>]*)>");
        Assert.True(rows.Count > 100);
        Assert.All(rows, row =>
        {
            var attributes = row.Groups["attributes"].Value;
            Assert.Matches("local:MenuRow\\.Icon=\"&#x[0-9A-F]{4};\"", attributes);
            var about = Regex.Match(attributes, "local:MenuRow\\.About=\"([^\"]+)\"");
            Assert.True(about.Success, row.Groups[1].Value);
            Assert.DoesNotContain('—', about.Groups[1].Value);
            Assert.DoesNotContain('–', about.Groups[1].Value);
        });
    }

    private static string Library([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "HandheldOptimiser.HomeLauncher", "Library"));
}
