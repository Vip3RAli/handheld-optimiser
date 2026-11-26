using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public sealed class LibraryBatch3ProfilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ho-batch3-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryBatch3ProfilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AutoTdp_FrameRatesInclude120HzVrrTargetsWithinProtocolBounds()
    {
        Assert.Equal([30, 40, 45, 50, 60, 72, 90, 120], AutoTdp.FrameRates);
        Assert.All(AutoTdp.FrameRates, fps => Assert.InRange(fps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps));
    }

    [Theory]
    [InlineData(nameof(AutoTdpState.Holding), 18, 5, true)]
    [InlineData(nameof(AutoTdpState.AtMaximum), 30, 2, true)]
    [InlineData(nameof(AutoTdpState.NoGain), 15, 10, true)]
    [InlineData(nameof(AutoTdpState.Holding), 18, 1, false)]
    [InlineData(nameof(AutoTdpState.TooHot), 22, 10, false)]
    [InlineData(nameof(AutoTdpState.Settling), 18, 10, false)]
    [InlineData(nameof(AutoTdpState.Waiting), 15, 10, false)]
    [InlineData(nameof(AutoTdpState.Raising), 20, 10, false)]
    [InlineData(nameof(AutoTdpState.Lowering), 20, 10, false)]
    [InlineData(AutoTdpProtocol.FixedState, 20, 10, false)]
    [InlineData(null, 20, 10, false)]
    [InlineData(nameof(AutoTdpState.Holding), 0, 10, false)]
    [InlineData(nameof(AutoTdpState.Holding), 35, 10, false)]
    [InlineData(nameof(AutoTdpState.Holding), null, 10, false)]
    public void AutoTdp_ShouldRememberWatts_RequiresSettledStateAndMinDuration(
        string? state, int? watts, int minutesPlayed, bool expected)
    {
        var status = new AutoTdpStatus(Running: true, Watts: watts, State: state);
        Assert.Equal(expected, AutoTdp.ShouldRememberWatts(status, TimeSpan.FromMinutes(minutesPlayed)));
    }

    [Fact]
    public void AutoTdp_DisplayNameHelpersFormatBoostAndControls()
    {
        Assert.Equal("Off", AutoTdp.CpuBoostName(AutoTdpProtocol.BoostOff));
        Assert.Equal("Efficient", AutoTdp.CpuBoostName(AutoTdpProtocol.BoostEfficient));
        Assert.Equal("Aggressive", AutoTdp.CpuBoostName(AutoTdpProtocol.BoostAggressive));
        Assert.Equal("Off", AutoTdp.CpuBoostName(null));

        Assert.Equal("Gamepad", AutoTdp.ControllerModeName(AutoTdpProtocol.GamepadMode));
        Assert.Equal("Desktop", AutoTdp.ControllerModeName(AutoTdpProtocol.DesktopMode));
        Assert.Equal("Gamepad", AutoTdp.ControllerModeName(null));
    }

    [Fact]
    public void GameProfiles_RoundTripBatch3FieldsAndVrrFrameRates()
    {
        var original = new GameProfile(
            PowerMode: PowerMode.Performance,
            RefreshRate: 120,
            Brightness: 70,
            CloseApps: true,
            Resolution: new Resolution(1920, 1080),
            AutoTdpFps: 72,
            FanCurve: AutoTdpProtocol.DynamicFans,
            CpuBoostMode: AutoTdpProtocol.BoostEfficient,
            AcBoostBursts: false,
            FreeSync: true,
            MaxWatts: 25,
            ControllerMode: AutoTdpProtocol.GamepadMode);

        var formatted = GameProfiles.Format(original);
        var parsed = GameProfiles.Parse(formatted);
        Assert.Equal(original, parsed);

        var description = parsed.Describe();
        Assert.Contains("72 fps", description);
        Assert.Contains("25 W max", description);
        Assert.Contains("CPU boost efficient", description);
        Assert.Contains("AC boost bursts off", description);
        Assert.Contains("FreeSync on", description);
        Assert.Contains("gamepad controls", description);
    }

    [Theory]
    [InlineData("boost=aggressive;acboost=on;freesync=off;watts=30;controls=desktop", AutoTdpProtocol.BoostAggressive, true, false, 30, AutoTdpProtocol.DesktopMode)]
    [InlineData("boost=TURBO;acboost=maybe;freesync=yes;watts=99;controls=mouse", null, null, null, null, null)]
    [InlineData("watts=6;boost=EFFICIENT;controls=GAMEPAD", AutoTdpProtocol.BoostEfficient, null, null, null, AutoTdpProtocol.GamepadMode)]
    public void GameProfiles_ParseValidatesNewKeysAndIgnoresInvalidValues(
        string text, string? boost, bool? acBoost, bool? freeSync, int? watts, string? controls)
    {
        var profile = GameProfiles.Parse(text);
        Assert.Equal(boost, profile.CpuBoostMode);
        Assert.Equal(acBoost, profile.AcBoostBursts);
        Assert.Equal(freeSync, profile.FreeSync);
        Assert.Equal(watts, profile.MaxWatts);
        Assert.Equal(controls, profile.ControllerMode);
    }

    [Fact]
    public void GameProfiles_ExportAndImportPresetRoundTripNewFields()
    {
        var game = new Game("steam:42", "Half-Life 3", GameStore.Steam, null, null, "steam://rungameid/42");
        var profile = new GameProfile(
            PowerMode: null,
            RefreshRate: null,
            Brightness: null,
            AutoTdpFps: 90,
            CpuBoostMode: AutoTdpProtocol.BoostOff,
            AcBoostBursts: true,
            FreeSync: true,
            MaxWatts: 18,
            ControllerMode: AutoTdpProtocol.DesktopMode);

        var exportedPath = GameProfiles.ExportPreset(game, profile, _folder);
        Assert.NotNull(exportedPath);
        Assert.True(File.Exists(exportedPath));

        var imported = GameProfiles.ImportPreset(exportedPath);
        Assert.Equal(profile, imported);
    }

    [Fact]
    public void GameProfile_DistinguishesDeviceOverridesFromSessionOnlySettings()
    {
        Assert.True(GameProfile.None.IsEmpty);
        Assert.False(GameProfile.None.HasAny);

        var sessionOnly = new GameProfile(null, null, null, MaxWatts: 20);
        Assert.True(sessionOnly.IsEmpty);
        Assert.True(sessionOnly.HasAny);

        Assert.False(new GameProfile(null, null, null, CpuBoostMode: AutoTdpProtocol.BoostEfficient).IsEmpty);
        Assert.False(new GameProfile(null, null, null, AcBoostBursts: false).IsEmpty);
        Assert.False(new GameProfile(null, null, null, FreeSync: true).IsEmpty);
        Assert.False(new GameProfile(null, null, null, ControllerMode: AutoTdpProtocol.GamepadMode).IsEmpty);
    }

    [Fact]
    public void MenuView_ExposesBatch3ProfileAndPowerCoolingRowsWithIconsAndDescriptions()
    {
        var xaml = File.ReadAllText(Path.Combine(LibraryPath(), "MenuView.xaml"));
        string[] expectedButtons =
        [
            "ProfileWattsSetting",
            "ProfileBoostSetting",
            "ProfileAcBoostSetting",
            "ProfileFreeSyncSetting",
            "ProfileControlsSetting",
            "CpuBoostSetting",
            "AcBoostSetting",
            "FreeSyncSetting"
        ];

        foreach (var name in expectedButtons)
        {
            var match = Regex.Match(xaml, $"<Button x:Name=\"{name}\" Style=\"\\{{StaticResource MenuButton\\}}\"(?<attributes>[^>]*)>");
            Assert.True(match.Success, $"Missing MenuButton {name}");
            var attrs = match.Groups["attributes"].Value;
            Assert.Matches("local:MenuRow\\.Icon=\"&#x[0-9A-F]{4};\"", attrs);
            Assert.Matches("local:MenuRow\\.About=\"[^\"]+\"", attrs);
        }
    }

    private static string LibraryPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "HandheldOptimiser.HomeLauncher", "Library"));
}
