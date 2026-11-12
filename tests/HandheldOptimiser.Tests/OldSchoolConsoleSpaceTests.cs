using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The Old School layout's Console space look: how far each card is turned and darkened, how much smaller
/// the stack is drawn to leave room for the floor, and that the room kept round a card in the XAML is the
/// room CardTilt is told about.
/// </summary>
public class OldSchoolConsoleSpaceTests
{
    [Fact]
    public void The_front_card_faces_the_player_and_the_rest_turn_further_the_further_back()
    {
        Assert.Equal(0, OldSchoolStack.Tilt(0));
        var tilts = Enumerable.Range(1, 5).Select(d => OldSchoolStack.Tilt(d)).ToList();
        Assert.All(tilts, t => Assert.InRange(t, 0.1, 0.3));
        Assert.Equal(tilts.OrderBy(t => t), tilts);
    }

    [Fact]
    public void A_card_moving_between_two_places_turns_part_way()
    {
        var between = OldSchoolStack.Tilt(1.5);
        Assert.InRange(between, OldSchoolStack.Tilt(1), OldSchoolStack.Tilt(2));
    }

    [Fact]
    public void Cards_further_back_are_darker_and_past_the_last_place_stay_as_the_last()
    {
        Assert.Equal(1, OldSchoolStack.Dim(0));
        Assert.True(OldSchoolStack.Dim(3) < OldSchoolStack.Dim(1));
        Assert.Equal(OldSchoolStack.Dim(5), OldSchoolStack.Dim(9));
        Assert.Equal(1, OldSchoolStack.Dim(-1));
    }

    [Fact]
    public void Console_space_draws_the_cards_smaller_on_a_wide_screen_only()
    {
        Assert.True(OldSchoolStack.Fit(3000, console: true) < OldSchoolStack.Fit(3000));
        Assert.Equal(OldSchoolStack.Fit(900), OldSchoolStack.Fit(900, console: true));
    }

    [Fact]
    public void The_room_kept_round_a_card_is_the_room_the_tilt_is_told_about()
    {
        var xaml = File.ReadAllText(Path.Combine(Library(), "LibraryResources.xaml"));
        var back = xaml[xaml.IndexOf("x:Key=\"ConsoleCardBack\"", StringComparison.Ordinal)..];
        var margin = Regex.Match(back, "<Rectangle Margin=\"(-?\\d+),(-?\\d+),(-?\\d+),(-?\\d+)\" Fill=\"Transparent\"").Groups;
        var (left, top, right, bottom) = (-double.Parse(margin[1].Value), -double.Parse(margin[2].Value),
            -double.Parse(margin[3].Value), -double.Parse(margin[4].Value));

        Assert.Equal(OldSchoolStack.FrameLeft, left);
        Assert.Equal(OldSchoolStack.FrameTop, top);
        Assert.Equal(OldSchoolStack.FrameWidth, left + OldSchoolStack.CardWidth + right);
        Assert.Equal(OldSchoolStack.FrameHeight, top + OldSchoolStack.CardHeight + bottom);
    }

    [Fact]
    public void The_compiled_tilt_is_in_the_repository_and_packed_into_the_library()
    {
        Assert.True(File.Exists(Path.Combine(Library(), "Shaders", "CardTilt.ps")));
        var project = File.ReadAllText(Path.Combine(Library(), "..", "HandheldOptimiser.HomeLauncher.csproj"));
        Assert.Contains("<Resource Include=\"Library\\Shaders\\CardTilt.ps\" />", project);
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
