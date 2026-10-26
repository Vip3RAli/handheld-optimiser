using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The stats screen on the Ally's own panel: what its tiles say, when they change colour, how the panel is
/// placed beside the monitor, and the names the service, the launcher and the widget share.
/// </summary>
public class ScreenStatsTests
{
    private static readonly OverlayReading Everything = new(
        Fps: 59.6, Watts: 15, Temperature: 71, Cpu: 42.4, Gpu: 97.8, MemoryUsedGb: 9.24, MemoryTotalGb: 15.4,
        Battery: 64, Charging: false, CpuGhz: 4.138, PowerDrawn: 11.6);

    // ----- The tiles -----

    [Fact]
    public void Six_tiles_in_a_fixed_order_with_no_battery() =>
        Assert.Equal(["FPS", "TDP", "TEMP", "CPU", "CPU CLOCK", "GPU"], ScreenStatsLayout.Tiles(Everything).Select(t => t.Label));

    [Fact]
    public void Values_are_rounded_with_their_units_apart()
    {
        var tiles = ScreenStatsLayout.Tiles(Everything);
        Assert.Equal(("60", ""), (tiles[0].Value, tiles[0].Unit));
        Assert.Equal(("15", "W"), (tiles[1].Value, tiles[1].Unit));
        Assert.Equal(("71", "°C"), (tiles[2].Value, tiles[2].Unit));
        Assert.Equal(("42", "%"), (tiles[3].Value, tiles[3].Unit));
        Assert.Equal(("4.1", "GHz"), (tiles[4].Value, tiles[4].Unit));
        Assert.Equal(("98", "%"), (tiles[5].Value, tiles[5].Unit));
    }

    [Fact]
    public void On_Auto_the_power_tile_shows_the_power_drawn()
    {
        var tile = ScreenStatsLayout.Tiles(Everything with { Watts = null })[1];
        Assert.Equal(("POWER", "12", "W"), (tile.Label, tile.Value, tile.Unit));
    }

    [Fact]
    public void A_reading_not_known_is_a_dash_with_no_unit_and_its_tile_stays()
    {
        var tiles = ScreenStatsLayout.Tiles(new OverlayReading());
        Assert.Equal(6, tiles.Count);
        Assert.Equal("TDP", tiles[1].Label);
        Assert.All(tiles, t => Assert.Equal(("–", ""), (t.Value, t.Unit)));
    }

    [Theory]
    [InlineData(79, "Normal")]
    [InlineData(80, "Warm")]
    [InlineData(89, "Warm")]
    [InlineData(90, "Hot")]
    public void The_temperature_turns_amber_then_red(int degrees, string tone) =>
        Assert.Equal(tone, ScreenStatsLayout.TemperatureTone(degrees).ToString());

    [Fact]
    public void The_time_follows_the_players_own_format()
    {
        var evening = new DateTime(2026, 10, 25, 21, 7, 30);
        Assert.Equal("21:07", ScreenStatsLayout.Clock(evening, CultureInfo.InvariantCulture));
        Assert.Equal("Sunday 25 October", ScreenStatsLayout.Date(evening, CultureInfo.InvariantCulture));
    }

    // ----- The panel's size and place -----

    [Fact]
    public void The_panel_goes_to_720p_when_it_offers_it() =>
        Assert.Equal((1280, 720), ScreenStatsLayout.PanelSize([(1920, 1080), (1280, 720), (800, 600)], 1920, 1080));

    [Fact]
    public void A_panel_without_720p_keeps_its_size() =>
        Assert.Equal((1920, 1080), ScreenStatsLayout.PanelSize([(1920, 1080), (1600, 900)], 1920, 1080));

    [Fact]
    public void The_panel_goes_centred_under_the_monitor_which_stays_at_the_corner()
    {
        var (monitors, panel) = ScreenStatsLayout.Arrange([new ScreenRect(0, 0, 1920, 1080)], 1280, 720);

        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), monitors[0]);
        Assert.Equal(new ScreenRect(320, 1080, 1280, 720), panel);
    }

    [Fact]
    public void A_monitor_Windows_put_elsewhere_is_moved_back_to_the_corner()
    {
        // Windows had put the panel at the corner and the monitor to its right.
        var (monitors, panel) = ScreenStatsLayout.Arrange([new ScreenRect(1280, 0, 1920, 1080)], 1280, 720);

        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), monitors[0]);
        Assert.Equal(new ScreenRect(320, 1080, 1280, 720), panel);
    }

    [Fact]
    public void Two_monitors_keep_their_places_and_the_panel_goes_under_both()
    {
        var (monitors, panel) = ScreenStatsLayout.Arrange(
            [new ScreenRect(-2560, -200, 2560, 1440), new ScreenRect(0, 0, 1920, 1080)], 1280, 720);

        Assert.Equal(new ScreenRect(-2560, -200, 2560, 1440), monitors[0]);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), monitors[1]);
        Assert.Equal(new ScreenRect(320, 1240, 1280, 720), panel);
    }

    // ----- Keeping other windows and the mouse off the panel -----

    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static readonly ScreenRect MonitorWork = new(0, 0, 1920, 1032);
    private static readonly ScreenRect Panel = new(320, 1080, 1280, 720);

    [Theory]
    [InlineData(400, 1100, 800, 600, true)]
    [InlineData(100, 100, 800, 600, false)]
    [InlineData(300, 800, 800, 600, true)]
    [InlineData(300, 600, 800, 600, false)]
    public void A_window_is_on_the_panel_when_its_middle_is(int x, int y, int width, int height, bool on) =>
        Assert.Equal(on, ScreenStatsLayout.IsOn(new ScreenRect(x, y, width, height), Panel));

    [Fact]
    public void A_window_keeps_its_place_from_the_corner_on_the_monitor() =>
        Assert.Equal(new ScreenRect(100, 50, 800, 600), ScreenStatsLayout.MoveOff(new ScreenRect(420, 1130, 800, 600), Panel, MonitorWork));

    [Fact]
    public void A_window_that_would_hang_off_the_monitor_is_kept_inside_it() =>
        Assert.Equal(new ScreenRect(0, 432, 800, 600), ScreenStatsLayout.MoveOff(new ScreenRect(200, 1700, 800, 600), Panel, MonitorWork));

    [Fact]
    public void A_window_bigger_than_the_monitor_is_made_to_fit() =>
        Assert.Equal(MonitorWork, ScreenStatsLayout.MoveOff(new ScreenRect(320, 1080, 2500, 1500), Panel, MonitorWork));

    [Fact]
    public void A_maximised_window_fills_the_monitor_with_the_same_border_past_each_edge() =>
        Assert.Equal(new ScreenRect(-8, -8, 1936, 1048), ScreenStatsLayout.Fill(new ScreenRect(312, 1072, 1296, 736), Panel, MonitorWork));

    [Fact]
    public void A_full_screen_window_fills_the_whole_monitor() =>
        Assert.Equal(Monitor, ScreenStatsLayout.Fill(Panel, Panel, Monitor));

    [Fact]
    public void The_mouse_is_kept_around_the_monitors_which_leaves_out_the_panel_under_them() =>
        Assert.Equal(new ScreenRect(-2560, -200, 4480, 1440),
            ScreenStatsLayout.CursorArea([new ScreenRect(-2560, -200, 2560, 1440), new ScreenRect(0, 0, 1920, 1080)], new ScreenRect(320, 1240, 1280, 720)));

    [Fact]
    public void A_panel_left_beside_the_monitors_keeps_the_mouse_on_the_main_monitor() =>
        Assert.Equal(Monitor, ScreenStatsLayout.CursorArea(
            [new ScreenRect(-1920, 400, 1920, 1080), Monitor], new ScreenRect(-1280, 0, 1280, 720)));

    [Fact]
    public void No_monitor_means_the_mouse_is_left_alone() =>
        Assert.Null(ScreenStatsLayout.CursorArea([], Panel));

    // ----- The note of what to put back -----

    private static readonly DateTime Started = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Off")]
    [InlineData("Shared")]
    public void A_note_from_since_Windows_started_is_used(string saved) =>
        Assert.Equal(saved, PanelNote.Parse(saved, Started.AddSeconds(40).Ticks, Started).ToString());

    [Fact]
    public void A_note_from_before_a_restart_is_not_used() =>
        Assert.Null(PanelNote.Parse("Off", Started.AddHours(-3).Ticks, Started));

    [Theory]
    [InlineData("Own")]
    [InlineData("Sideways")]
    [InlineData(null)]
    public void A_note_this_app_would_not_write_is_not_used(string? saved) =>
        Assert.Null(PanelNote.Parse(saved, Started.Ticks, Started));

    [Fact]
    public void A_note_without_its_time_is_not_used() =>
        Assert.Null(PanelNote.Parse("Off", null, Started));

    // ----- The names they share -----

    [Fact]
    public void The_service_and_the_launcher_agree_on_the_argument_and_mutex()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.PowerService", "ScreenStatsHost.cs"));

        Assert.Contains($"ScreenStatsArgument = \"{HomeLauncher.Program.ScreenStatsArgument}\"", host);
        Assert.Contains($"MutexName = \"{ScreenStatsApp.MutexName}\"", host);
    }

    [Fact]
    public void The_widget_has_the_switch_and_sends_the_service_its_command()
    {
        var widget = Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar");
        Assert.Matches("<ToggleSwitch\\b[^>]*Toggled=\"OnScreenStats\"", File.ReadAllText(Path.Combine(widget, "TdpWidget.xaml")));
        Assert.Contains("AutoTdpProtocol.SetScreenStats", File.ReadAllText(Path.Combine(widget, "EngineClient.cs")));
    }

    [Fact]
    public void A_request_to_switch_it_on_reads_back()
    {
        var request = AutoTdpProtocol.Read<AutoTdpRequest>(
            $"{{\"{AutoTdpProtocol.Fields.Command}\":\"{AutoTdpProtocol.SetScreenStats}\",\"{AutoTdpProtocol.Fields.ScreenStats}\":true}}");

        Assert.Equal(AutoTdpProtocol.SetScreenStats, request?.Command);
        Assert.True(request?.ScreenStats);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
