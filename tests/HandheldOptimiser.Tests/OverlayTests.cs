using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The performance overlay: what each level shows, where it sits, what a set-overlay request comes to,
/// and the widget's tiles against the service's names.
/// </summary>
public class OverlayTests
{
    private static readonly OverlayReading Everything = new(
        Fps: 59.6, Watts: 15, Temperature: 71, Cpu: 42.4, Gpu: 97.8, MemoryUsedGb: 9.24, MemoryTotalGb: 15.4,
        Battery: 64, Charging: false);

    private static string[] Labels(string level, OverlayReading reading) =>
        OverlayLayout.Lines(level, reading).Select(l => l.Label).ToArray();

    private static string Value(string level, OverlayReading reading, string label) =>
        OverlayLayout.Lines(level, reading).Single(l => l.Label == label).Value;

    // ----- What each level shows -----

    [Fact]
    public void Small_is_the_frame_rate_alone() =>
        Assert.Equal(["FPS"], Labels(AutoTdpProtocol.OverlaySmall, Everything));

    [Fact]
    public void Medium_adds_the_power_limit_use_and_temperature() =>
        Assert.Equal(["FPS", "TDP", "CPU", "GPU", "Temp"], Labels(AutoTdpProtocol.OverlayMedium, Everything));

    [Fact]
    public void Full_adds_frame_time_memory_and_battery() =>
        Assert.Equal(["FPS", "TDP", "CPU", "GPU", "Temp", "Frame", "RAM", "Battery"], Labels(AutoTdpProtocol.OverlayFull, Everything));

    [Fact]
    public void No_power_limit_or_temperature_leaves_their_rows_out() =>
        Assert.Equal(["FPS", "CPU", "GPU"], Labels(AutoTdpProtocol.OverlayMedium, Everything with { Watts = null, Temperature = null }));

    [Fact]
    public void No_battery_leaves_its_row_out() =>
        Assert.DoesNotContain("Battery", Labels(AutoTdpProtocol.OverlayFull, Everything with { Battery = null }));

    // ----- How the values read -----

    [Fact]
    public void Values_are_rounded_and_carry_their_units()
    {
        Assert.Equal("60", Value(AutoTdpProtocol.OverlayFull, Everything, "FPS"));
        Assert.Equal("15 W", Value(AutoTdpProtocol.OverlayFull, Everything, "TDP"));
        Assert.Equal("42%", Value(AutoTdpProtocol.OverlayFull, Everything, "CPU"));
        Assert.Equal("98%", Value(AutoTdpProtocol.OverlayFull, Everything, "GPU"));
        Assert.Equal("71 °C", Value(AutoTdpProtocol.OverlayFull, Everything, "Temp"));
        Assert.Equal("16.8 ms", Value(AutoTdpProtocol.OverlayFull, Everything, "Frame"));
        Assert.Equal("9.2 / 15 GB", Value(AutoTdpProtocol.OverlayFull, Everything, "RAM"));
        Assert.Equal("64%", Value(AutoTdpProtocol.OverlayFull, Everything, "Battery"));
    }

    [Fact]
    public void Readings_not_known_show_a_question_mark()
    {
        var nothing = new OverlayReading();
        Assert.Equal("?", Value(AutoTdpProtocol.OverlayFull, nothing, "FPS"));
        Assert.Equal("?", Value(AutoTdpProtocol.OverlayFull, nothing, "CPU"));
        Assert.Equal("?", Value(AutoTdpProtocol.OverlayFull, nothing, "Frame"));
        Assert.Equal("?", Value(AutoTdpProtocol.OverlayFull, nothing, "RAM"));
    }

    [Fact]
    public void No_frames_gives_no_frame_time() =>
        Assert.Equal("?", Value(AutoTdpProtocol.OverlayFull, Everything with { Fps = 0 }, "Frame"));

    [Fact]
    public void Charging_says_so_beside_the_charge() =>
        Assert.Equal("64%, charging", Value(AutoTdpProtocol.OverlayFull, Everything with { Charging = true }, "Battery"));

    [Fact]
    public void Use_over_a_hundred_is_shown_as_a_hundred() =>
        Assert.Equal("100%", Value(AutoTdpProtocol.OverlayMedium, Everything with { Gpu = 140 }, "GPU"));

    // ----- The frame rate's average -----

    [Fact]
    public void The_frame_rate_is_the_average_of_the_last_three_seconds()
    {
        var average = new FpsAverage();
        Assert.Equal(60, average.Add(7, 60));
        Assert.Equal(45, average.Add(7, 30));
        Assert.Equal(60, average.Add(7, 90));
        Assert.Equal(50, average.Add(7, 30));
    }

    [Fact]
    public void Another_program_in_front_starts_the_average_again()
    {
        var average = new FpsAverage();
        average.Add(7, 30);
        average.Add(7, 30);
        Assert.Equal(144, average.Add(8, 144));
    }

    [Fact]
    public void A_second_with_no_count_shows_nothing_and_starts_again()
    {
        var average = new FpsAverage();
        average.Add(7, 30);
        Assert.Null(average.Add(7, null));
        Assert.Equal(60, average.Add(7, 60));
    }

    // ----- Where it sits -----

    [Theory]
    [InlineData(AutoTdpProtocol.TopLeft, 12, 12)]
    [InlineData(AutoTdpProtocol.TopRight, 1280 - 200 - 12, 12)]
    [InlineData(AutoTdpProtocol.BottomLeft, 12, 800 - 100 - 12)]
    [InlineData(AutoTdpProtocol.BottomRight, 1280 - 200 - 12, 800 - 100 - 12)]
    public void Each_corner_keeps_the_margin_from_its_edges(string corner, double left, double top) =>
        Assert.Equal((left, top), OverlayLayout.Place(corner, 1280, 800, 200, 100));

    [Fact]
    public void A_box_wider_than_the_screen_stays_on_it() =>
        Assert.Equal((0d, 0d), OverlayLayout.Place(AutoTdpProtocol.BottomRight, 100, 50, 300, 200));

    // ----- What a request comes to -----

    [Fact]
    public void A_level_or_corner_not_known_keeps_the_one_there_is()
    {
        Assert.Equal(AutoTdpProtocol.OverlayMedium, OverlayRules.Level("huge", AutoTdpProtocol.OverlayMedium));
        Assert.Equal(AutoTdpProtocol.BottomRight, OverlayRules.Corner("middle", AutoTdpProtocol.BottomRight));
    }

    [Fact]
    public void Leaving_one_out_keeps_it()
    {
        Assert.Equal(AutoTdpProtocol.OverlayFull, OverlayRules.Level(null, AutoTdpProtocol.OverlayFull));
        Assert.Equal(AutoTdpProtocol.TopRight, OverlayRules.Corner(null, AutoTdpProtocol.TopRight));
    }

    [Fact]
    public void A_known_level_or_corner_is_taken()
    {
        Assert.Equal(AutoTdpProtocol.OverlaySmall, OverlayRules.Level(AutoTdpProtocol.OverlaySmall, AutoTdpProtocol.OverlayOff));
        Assert.Equal(AutoTdpProtocol.BottomLeft, OverlayRules.Corner(AutoTdpProtocol.BottomLeft, AutoTdpProtocol.TopLeft));
    }

    [Fact]
    public void Nothing_saved_starts_off_in_the_top_left_one_row_under_another()
    {
        Assert.Equal(AutoTdpProtocol.OverlayOff, OverlayRules.SavedLevel(null));
        Assert.Equal(AutoTdpProtocol.TopLeft, OverlayRules.SavedCorner("garbage"));
        Assert.Equal(AutoTdpProtocol.OverlayVertical, OverlayRules.SavedLayout(null));
    }

    [Fact]
    public void A_layout_is_taken_when_known_and_kept_otherwise()
    {
        Assert.Equal(AutoTdpProtocol.OverlayHorizontal, OverlayRules.Layout(AutoTdpProtocol.OverlayHorizontal, AutoTdpProtocol.OverlayVertical));
        Assert.Equal(AutoTdpProtocol.OverlayHorizontal, OverlayRules.Layout(null, AutoTdpProtocol.OverlayHorizontal));
        Assert.Equal(AutoTdpProtocol.OverlayVertical, OverlayRules.Layout("diagonal", AutoTdpProtocol.OverlayVertical));
    }

    // ----- The widget's tiles -----

    [Fact]
    public void The_widgets_overlay_and_boost_tiles_name_choices_the_service_knows()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "TdpWidget.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "TdpWidget.xaml.cs"));

        // The overlay size is a slider whose stops are the protocol's own names, in order (since 2026-10-28).
        Assert.Equal([AutoTdpProtocol.OverlayOff, AutoTdpProtocol.OverlaySmall, AutoTdpProtocol.OverlayMedium, AutoTdpProtocol.OverlayFull],
            AutoTdpProtocol.OverlayLevels);
        Assert.Contains("AutoTdpProtocol.OverlayOff, AutoTdpProtocol.OverlaySmall, AutoTdpProtocol.OverlayMedium, AutoTdpProtocol.OverlayFull", code);
        Assert.Equal(AutoTdpProtocol.OverlayCorners, Tags(xaml, "OnOverlayCorner"));
        Assert.Equal(AutoTdpProtocol.OverlayLayouts, Tags(xaml, "OnOverlayLayout"));
        Assert.Matches("<ToggleSwitch\\b[^>]*Toggled=\"OnCpuBoost\"", xaml);
    }

    [Fact]
    public void The_service_and_the_launcher_agree_on_the_overlay_argument_and_mutex()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.PowerService", "OverlayHost.cs"));

        Assert.Contains($"OverlayArgument = \"{HomeLauncher.Program.OverlayArgument}\"", host);
        Assert.Contains($"MutexName = \"{OverlayApp.MutexName}\"", host);
    }

    private static List<string> Tags(string xaml, string handler) =>
        Regex.Matches(xaml, $"<ToggleButton\\b[^>]*?Click=\"{handler}\"[^>]*>")
            .Select(m => Regex.Match(m.Value, "Tag=\"([^\"]*)\"").Groups[1].Value)
            .ToList();

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
