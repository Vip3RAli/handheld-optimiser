using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.Models;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Unit tests for Battery Care (80% charge limit), back paddle actions, thumbstick RGB ring controls,
/// estimated playtime remaining, and Steam shortcuts.vdf import.
/// </summary>
public sealed class SummaryRecommendationsTests
{
    // ----- 1. Battery Care (80% Charge Limit) -----

    [Fact]
    public void Battery_care_maps_toggle_state_to_80_and_100_percent()
    {
        Assert.Equal(80, BatteryCare.LimitFor(on: true));
        Assert.Equal(100, BatteryCare.LimitFor(on: false));
        Assert.True(BatteryCare.IsEnabled(80));
        Assert.False(BatteryCare.IsEnabled(100));
        Assert.False(BatteryCare.IsEnabled(null));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void TDP_Engine_starts_with_Windows_while_the_limit_is_on_and_never_stops_doing_so_while_replacing_Armoury_Crate(
        bool careOn, bool replacing, bool expected) =>
        Assert.Equal(expected, BatteryCare.StartsWithWindows(careOn, replacing));

    [Fact]
    public async Task Battery_care_tweak_applies_and_reverts_cleanly()
    {
        var log = TestLog.Instance;
        var registry = new FakeRegistry();
        var journal = new FakeJournal();
        var runner = new PowerShellRunner(log);
        var restorePoints = new RestorePointService(log, runner, new RegistryHelper(log));
        var engine = new TweakEngine(log, registry, runner, restorePoints, journal, HandheldDevice.RogAlly);

        var tweak = SleepTweaks.BatteryCare;
        Assert.Equal(HandheldDevice.RogAlly, tweak.OnlyFor);
        Assert.False(tweak.IncludeInOneClick);
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));

        var summary = await engine.ApplyAsync([tweak], "test", createRestorePoint: false);
        Assert.Equal(0, summary.Failures);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        Assert.Equal(80, registry.ReadValue(RegistryRoot.LocalMachine, BatteryCare.SettingsKey, BatteryCare.LimitValue));

        var reverted = await engine.RevertAsync(tweak);
        Assert.False(reverted.IsFailure);
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
        Assert.Null(registry.ReadValue(RegistryRoot.LocalMachine, BatteryCare.SettingsKey, BatteryCare.LimitValue));
    }

    // ----- 2. Back Paddle (M1/M2) Actions -----

    [Theory]
    [InlineData(AutoTdpProtocol.PaddleNone)]
    [InlineData(AutoTdpProtocol.PaddleOverlay)]
    [InlineData(AutoTdpProtocol.PaddleMode)]
    [InlineData(AutoTdpProtocol.PaddleKeyboard)]
    [InlineData(AutoTdpProtocol.PaddleScreenshot)]
    [InlineData(AutoTdpProtocol.PaddleMute)]
    public void Known_paddle_actions_are_accepted_case_insensitively(string action)
    {
        Assert.True(AllyCommands.IsPaddleAction(action));
        Assert.True(AllyCommands.IsPaddleAction(action.ToUpperInvariant()));
        Assert.Equal(action, AllyCommands.ParsePaddleAction(action.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("macro")]
    public void Unknown_paddle_action_falls_back_to_none(string? action)
    {
        Assert.False(AllyCommands.IsPaddleAction(action));
        Assert.Equal(AutoTdpProtocol.PaddleNone, AllyCommands.ParsePaddleAction(action));
    }

    // ----- 3. Stick RGB Ring Controls -----

    [Theory]
    [InlineData(AutoTdpProtocol.LightingOff, 0)]
    [InlineData(AutoTdpProtocol.LightingLow, 1)]
    [InlineData(AutoTdpProtocol.LightingMedium, 2)]
    [InlineData(AutoTdpProtocol.LightingHigh, 3)]
    [InlineData(AutoTdpProtocol.LightingBattery, 2)]
    public void Lighting_modes_map_to_hardware_brightness_steps(string mode, byte expectedBrightness)
    {
        Assert.True(AllyCommands.IsLightingMode(mode));
        Assert.Equal(mode, AllyCommands.ParseLighting(mode.ToUpperInvariant()));
        Assert.Equal(expectedBrightness, AllyCommands.BrightnessLevel(mode));
    }

    [Fact]
    public void Battery_lighting_colour_follows_charge_bands()
    {
        Assert.Equal((byte)0xE8, AllyCommands.BatteryColour(15).R);
        Assert.Equal((byte)0xFF, AllyCommands.BatteryColour(35).R);
        Assert.Equal(AllyCommands.DefaultColour, AllyCommands.BatteryColour(80));
        Assert.Equal(AllyCommands.DefaultColour, AllyCommands.BatteryColour(null));
    }

    [Fact]
    public void Lighting_off_sends_zero_brightness_and_active_modes_send_colour_and_commit()
    {
        var offReports = AllyCommands.LightingReports(AutoTdpProtocol.LightingOff);
        var single = Assert.Single(offReports);
        Assert.Equal(AllyCommands.ReportId, single[0]);
        Assert.Equal(0xBA, single[1]);
        Assert.Equal(0, single[4]);

        var highReports = AllyCommands.LightingReports(AutoTdpProtocol.LightingHigh);
        Assert.Equal(4, highReports.Length);
        Assert.Equal(3, highReports[0][4]);
        Assert.Equal(0xB3, highReports[1][1]);
        Assert.Equal(0xB5, highReports[2][1]);
        Assert.Equal(0xB4, highReports[3][1]);
    }

    [Theory]
    [InlineData(AutoTdpProtocol.ColourRed, 0xFF, 0x00, 0x00)]
    [InlineData(AutoTdpProtocol.ColourWhite, 0xFF, 0xFF, 0xFF)]
    [InlineData(AutoTdpProtocol.ColourGreen, 0x10, 0x7C, 0x10)]
    [InlineData("not a colour", 0x10, 0x7C, 0x10)]
    public void The_chosen_colour_is_what_the_colour_report_carries(string colour, byte r, byte g, byte b)
    {
        var colourReport = AllyCommands.LightingReports(AutoTdpProtocol.LightingMedium, null, colour)[1];
        Assert.Equal(0xB3, colourReport[1]);
        Assert.Equal([r, g, b], colourReport[4..7]);
    }

    [Fact]
    public void Battery_mode_colours_by_the_charge_whatever_colour_is_chosen()
    {
        var colourReport = AllyCommands.LightingReports(AutoTdpProtocol.LightingBattery, 10, AutoTdpProtocol.ColourBlue)[1];
        var (r, g, b) = AllyCommands.BatteryColour(10);
        Assert.Equal([r, g, b], colourReport[4..7]);
    }

    [Fact]
    public void Every_colour_has_its_own_mix_and_the_widget_offers_them_all_in_order()
    {
        Assert.Equal(AutoTdpProtocol.LightingColours.Length, AutoTdpProtocol.LightingColours.Select(AllyCommands.Rgb).Distinct().Count());
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "TdpWidget.xaml.cs"));
        var offered = System.Text.RegularExpressions.Regex.Matches(code, @"AutoTdpProtocol\.Colour(\w+)")
            .Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToArray();
        Assert.Equal(AutoTdpProtocol.LightingColours, offered);
    }

    [Theory]
    [InlineData("Elden Ring", "Elden Ring 2026-11-11 19-30-05.png")]
    [InlineData("Game: Part 2?", "Game Part 2 2026-11-11 19-30-05.png")]
    [InlineData(null, "Screenshot 2026-11-11 19-30-05.png")]
    [InlineData("  ", "Screenshot 2026-11-11 19-30-05.png")]
    public void A_screenshot_is_named_after_the_window_and_the_time(string? title, string expected) =>
        Assert.Equal(expected, Screenshot.FileName(title, new DateTime(2026, 11, 11, 19, 30, 5)));

    [Fact]
    public void Widget_paddle_and_lighting_choices_match_the_service()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "TdpWidget.xaml.cs"));
        Assert.Contains("AutoTdpProtocol.LightingOff, AutoTdpProtocol.LightingLow, AutoTdpProtocol.LightingMedium, AutoTdpProtocol.LightingHigh, AutoTdpProtocol.LightingBattery", code);
        foreach (var action in AutoTdpProtocol.PaddleActions)
        {
            Assert.Contains($"\"{action}\"", File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.PowerService", "AutoTdpProtocol.Names.cs")));
        }
    }

    // ----- 4. Estimated Playtime Remaining -----

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-10, null)]
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    [InlineData(82, "1h 22m")]
    [InlineData(150, "2h 30m")]
    public void Duration_formats_as_hours_and_minutes(int? minutes, string? expected)
    {
        Assert.Equal(expected, BatteryReading.FormatDuration(minutes));
        Assert.Equal(expected, OverlayLayout.FormatDuration(minutes));
    }

    [Fact]
    public void Battery_reading_estimates_playtime_only_while_discharging()
    {
        var discharging = new BatteryReading(OnMains: false, Charging: false, FullChargedCapacity: 40000, RemainingCapacity: 20000, Rate: 15000);
        Assert.Equal(80, discharging.RemainingMinutes);
        Assert.Equal("1h 20m", discharging.RemainingTime);

        var onMains = discharging with { OnMains = true };
        Assert.Null(onMains.RemainingMinutes);
        Assert.Null(onMains.RemainingTime);

        var charging = discharging with { Charging = true };
        Assert.Null(charging.RemainingMinutes);

        var unknownRate = discharging with { Rate = null };
        Assert.Null(unknownRate.RemainingMinutes);
    }

    [Fact]
    public void Full_overlay_shows_remaining_playtime_when_discharging()
    {
        var reading = new OverlayReading(Fps: 60, Battery: 64, Charging: false, BatteryMinutes: 82);
        var batteryLine = OverlayLayout.Lines(AutoTdpProtocol.OverlayFull, reading).Single(l => l.Label == "Battery");
        Assert.Equal("64%, 1h 22m", batteryLine.Value);
    }

    // ----- 5. Steam shortcuts.vdf Import -----

    [Theory]
    [InlineData("\"C:\\Games\\Hades\\Hades.exe\"", null, @"C:\Games\Hades\Hades.exe", null)]
    [InlineData("\"C:\\Games\\Hades\\Hades.exe\" /vulkan", "-fullscreen", @"C:\Games\Hades\Hades.exe", "/vulkan -fullscreen")]
    [InlineData("C:\\Games\\Celeste\\Celeste.exe", "--skip-splash", @"C:\Games\Celeste\Celeste.exe", "--skip-splash")]
    public void Split_target_separates_quoted_executable_and_combines_arguments(
        string rawExe, string? launchOptions, string expectedExe, string? expectedArgs)
    {
        var (exe, args) = SteamShortcuts.SplitTarget(rawExe, launchOptions);
        Assert.Equal(expectedExe, exe);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void Parse_shortcuts_reads_binary_vdf_and_skips_hidden_entries()
    {
        var vdf = BuildShortcutsVdf(
            (0x90000001u, "Hollow Knight", "\"C:\\Games\\Hollow Knight\\hollow_knight.exe\"", "-popupwindow", "\"C:\\Games\\Hollow Knight\"", false),
            (0x90000002u, "Hidden Tool", "\"C:\\Tools\\tool.exe\"", "", "\"C:\\Tools\"", true));

        var shortcuts = SteamShortcuts.ParseShortcuts(vdf);
        var single = Assert.Single(shortcuts);
        Assert.Equal(0x90000001u, single.AppId);
        Assert.Equal("Hollow Knight", single.Name);
        Assert.Equal(@"C:\Games\Hollow Knight\hollow_knight.exe", single.Executable);
        Assert.Equal("-popupwindow", single.Arguments);
        Assert.Equal(@"C:\Games\Hollow Knight", single.WorkingDirectory);
    }

    [Fact]
    public void Compute_app_id_sets_the_top_bit_as_steam_does()
    {
        var id = SteamShortcuts.ComputeAppId("\"C:\\Games\\Game.exe\"", "Game");
        Assert.True((id & 0x80000000u) != 0);
    }

    private static byte[] BuildShortcutsVdf(
        params (uint AppId, string Name, string Exe, string LaunchOptions, string StartDir, bool Hidden)[] items)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0);
        WriteCString(ms, "shortcuts");

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            ms.WriteByte(0);
            WriteCString(ms, i.ToString());

            WriteInt32(ms, "appid", unchecked((int)item.AppId));
            WriteString(ms, "AppName", item.Name);
            WriteString(ms, "Exe", item.Exe);
            WriteString(ms, "StartDir", item.StartDir);
            WriteString(ms, "LaunchOptions", item.LaunchOptions);
            WriteInt32(ms, "IsHidden", item.Hidden ? 1 : 0);

            ms.WriteByte(8);
        }

        ms.WriteByte(8);
        ms.WriteByte(8);
        return ms.ToArray();
    }

    private static void WriteString(Stream stream, string name, string value)
    {
        stream.WriteByte(1);
        WriteCString(stream, name);
        WriteCString(stream, value);
    }

    private static void WriteInt32(Stream stream, string name, int value)
    {
        stream.WriteByte(2);
        WriteCString(stream, name);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteCString(Stream stream, string text)
    {
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.WriteByte(0);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
