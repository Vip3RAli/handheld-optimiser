using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// TDP Engine's Auto mode: the range a session gets, a frame rate being optional, and the requests the
/// widget sends to switch it.
/// </summary>
public class AutoModeTests
{
    [Fact]
    public void The_top_is_30_W_plugged_in_and_25_on_battery()
    {
        Assert.Equal(30, AutoMode.TopWatts(onBattery: false));
        Assert.Equal(25, AutoMode.TopWatts(onBattery: true));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("auto", true)]
    [InlineData("anything", true)]
    [InlineData("manual", false)]
    public void Only_manual_is_manual(string? mode, bool auto) =>
        Assert.Equal(auto, AutoMode.IsAuto(mode));

    [Fact]
    public void A_frame_rate_is_kept_and_the_range_is_Autos() =>
        Assert.Equal(new AutoTdpSettings(60, AutoTdpProtocol.LowestWatts, 30, AutoTdpProtocol.DefaultTemperature),
            AutoMode.Settings(60, 0, onBattery: false));

    [Fact]
    public void A_frame_rate_past_the_limits_is_kept_within_them() =>
        Assert.Equal(AutoTdpProtocol.HighestFps, AutoMode.Settings(500, 0, onBattery: false).TargetFps);

    [Fact]
    public void A_temperature_limit_asked_for_is_kept() =>
        Assert.Equal(75, AutoMode.Settings(60, 75, onBattery: false).TemperatureLimit);

    [Fact]
    public void No_frame_rate_is_no_target()
    {
        var settings = AutoMode.Settings(0, 0, onBattery: true);
        Assert.Equal(AutoTdpProtocol.NoTargetFps, settings.TargetFps);
        Assert.Equal(25, settings.MaxWatts);
    }

    [Theory]
    [InlineData(AutoTdpProtocol.NoTargetFps, 0)]
    [InlineData(60, 60)]
    public void No_target_is_shown_as_0(int target, int shown) =>
        Assert.Equal(shown, AutoMode.ShownTarget(target));

    [Fact]
    public void A_request_to_set_the_mode_reads_back()
    {
        var request = AutoTdpProtocol.Read<AutoTdpRequest>(
            $"{{\"{AutoTdpProtocol.Fields.Command}\":\"{AutoTdpProtocol.SetMode}\",\"{AutoTdpProtocol.Fields.Mode}\":\"{AutoTdpProtocol.ManualMode}\"}}");

        Assert.Equal(AutoTdpProtocol.SetMode, request?.Command);
        Assert.Equal(AutoTdpProtocol.ManualMode, request?.Mode);
    }

    [Fact]
    public void A_status_carries_the_mode_and_the_manual_settings()
    {
        var json = AutoTdpProtocol.Write(new AutoTdpStatus(false, Mode: AutoTdpProtocol.ManualMode, ManualPowerLimit: 18,
            ManualFans: AutoTdpProtocol.CoolFans));
        var status = AutoTdpProtocol.Read<AutoTdpStatus>(json);

        Assert.Contains($"\"{AutoTdpProtocol.Fields.Mode}\":\"manual\"", json);
        Assert.Contains($"\"{AutoTdpProtocol.Fields.ManualPowerLimit}\":18", json);
        Assert.Contains($"\"{AutoTdpProtocol.Fields.ManualFans}\":\"cool\"", json);
        Assert.Equal((AutoTdpProtocol.ManualMode, 18, AutoTdpProtocol.CoolFans), (status?.Mode, status?.ManualPowerLimit, status?.ManualFans));
    }
}
