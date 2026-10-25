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
        Battery: 64, Charging: false);

    // ----- The tiles -----

    [Fact]
    public void Six_tiles_in_a_fixed_order() =>
        Assert.Equal(["FPS", "TDP", "TEMP", "CPU", "GPU", "BATTERY"], ScreenStatsLayout.Tiles(Everything).Select(t => t.Label));

    [Fact]
    public void Values_are_rounded_with_their_units_apart()
    {
        var tiles = ScreenStatsLayout.Tiles(Everything);
        Assert.Equal(("60", ""), (tiles[0].Value, tiles[0].Unit));
        Assert.Equal(("15", "W"), (tiles[1].Value, tiles[1].Unit));
        Assert.Equal(("71", "°C"), (tiles[2].Value, tiles[2].Unit));
        Assert.Equal(("42", "%"), (tiles[3].Value, tiles[3].Unit));
        Assert.Equal(("98", "%"), (tiles[4].Value, tiles[4].Unit));
        Assert.Equal(("64", "%"), (tiles[5].Value, tiles[5].Unit));
    }

    [Fact]
    public void A_reading_not_known_is_a_dash_with_no_unit_and_its_tile_stays()
    {
        var tiles = ScreenStatsLayout.Tiles(new OverlayReading());
        Assert.Equal(6, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(("–", ""), (t.Value, t.Unit)));
    }

    [Fact]
    public void Charging_names_the_battery_tile_so() =>
        Assert.Equal("CHARGING", ScreenStatsLayout.Tiles(Everything with { Charging = true })[5].Label);

    [Theory]
    [InlineData(79, "Normal")]
    [InlineData(80, "Warm")]
    [InlineData(89, "Warm")]
    [InlineData(90, "Hot")]
    public void The_temperature_turns_amber_then_red(int degrees, string tone) =>
        Assert.Equal(tone, ScreenStatsLayout.TemperatureTone(degrees).ToString());

    [Theory]
    [InlineData(26, false, "Normal")]
    [InlineData(25, false, "Warm")]
    [InlineData(15, false, "Hot")]
    [InlineData(5, true, "Normal")]
    public void A_low_battery_turns_amber_then_red_unless_charging(int charge, bool charging, string tone) =>
        Assert.Equal(tone, ScreenStatsLayout.BatteryTone(charge, charging).ToString());

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
