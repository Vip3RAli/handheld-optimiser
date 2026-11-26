using HandheldOptimiser.Models;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

public class AllyPerformanceTweaksTests
{
    [Theory]
    [InlineData(AutoTdpProtocol.BoostOff, CpuBoostRules.Disabled)]
    [InlineData(AutoTdpProtocol.BoostEfficient, CpuBoostRules.EfficientEnabled)]
    [InlineData(AutoTdpProtocol.BoostAggressive, CpuBoostRules.Aggressive)]
    public void CpuBoostRules_TargetForMode_sets_plugged_in_and_battery_modes(string mode, uint expected)
    {
        var saved = new BoostModes(CpuBoostRules.Aggressive, CpuBoostRules.Aggressive);
        Assert.Equal(new BoostModes(expected, expected), CpuBoostRules.TargetForMode(mode, saved));
    }

    [Theory]
    [InlineData(CpuBoostRules.Disabled, CpuBoostRules.Aggressive, false, AutoTdpProtocol.BoostOff)]
    [InlineData(CpuBoostRules.Disabled, CpuBoostRules.Aggressive, true, AutoTdpProtocol.BoostAggressive)]
    [InlineData(CpuBoostRules.EfficientEnabled, CpuBoostRules.Disabled, false, AutoTdpProtocol.BoostEfficient)]
    [InlineData(CpuBoostRules.EfficientAggressive, CpuBoostRules.Disabled, false, AutoTdpProtocol.BoostEfficient)]
    [InlineData(6u, CpuBoostRules.Disabled, false, AutoTdpProtocol.BoostEfficient)]
    [InlineData(1u, CpuBoostRules.Disabled, false, AutoTdpProtocol.BoostAggressive)]
    [InlineData(CpuBoostRules.Aggressive, CpuBoostRules.Disabled, false, AutoTdpProtocol.BoostAggressive)]
    public void CpuBoostRules_ModeName_maps_raw_windows_indices(uint ac, uint dc, bool onBattery, string expected)
    {
        Assert.Equal(expected, CpuBoostRules.ModeName(new BoostModes(ac, dc), onBattery));
    }

    [Theory]
    [InlineData(30, false, false, 65, 80, 30, 30, 30)]
    [InlineData(30, true, true, 65, 80, 30, 30, 30)]
    [InlineData(30, true, false, 77, 80, 30, 30, 30)]
    [InlineData(30, true, false, 70, 80, 30, 43, 53)]
    [InlineData(15, true, false, 65, 80, 15, 28, 38)]
    public void PowerLimits_Envelope_respects_ac_battery_and_thermal_margin(
        int watts, bool allowAcBoost, bool onBattery, int temp, int limit,
        int expectedSpl, int expectedSppt, int expectedFppt)
    {
        var envelope = PowerLimits.Envelope(watts, allowAcBoost, onBattery, temp, limit);
        Assert.Equal(new PowerEnvelope(expectedSpl, expectedSppt, expectedFppt), envelope);
    }

    [Fact]
    public void RadeonState_round_trips_freesync_flag_and_accepts_freesync_switch()
    {
        var line = new RadeonState { Rsr = true, FreeSync = true }.Format();
        Assert.Equal("rsr=1 freesync=1", line);

        var parsed = RadeonState.Parse(line);
        Assert.NotNull(parsed);
        Assert.True(parsed!.FreeSync);

        Assert.True(RadeonState.IsChange(RadeonState.FreeSyncName, true, null));
        Assert.False(RadeonState.IsChange(RadeonState.FreeSyncName, null, 60));
    }

    [Fact]
    public void AutoTdpProtocol_round_trips_boost_mode_ac_boost_and_freesync()
    {
        var request = new AutoTdpRequest(
            AutoTdpProtocol.SetAcBoost,
            CpuBoostMode: AutoTdpProtocol.BoostEfficient,
            AcBoostBursts: true);
        var requestJson = AutoTdpProtocol.Write(request);
        var roundTrippedRequest = AutoTdpProtocol.Read<AutoTdpRequest>(requestJson);

        Assert.NotNull(roundTrippedRequest);
        Assert.Equal(AutoTdpProtocol.BoostEfficient, roundTrippedRequest!.CpuBoostMode);
        Assert.True(roundTrippedRequest.AcBoostBursts);

        var status = new AutoTdpStatus(
            Running: true,
            CpuBoost: true,
            CpuBoostMode: AutoTdpProtocol.BoostEfficient,
            AcBoostBursts: true,
            FreeSync: true);
        var statusJson = AutoTdpProtocol.Write(status);
        var roundTrippedStatus = AutoTdpProtocol.Read<AutoTdpStatus>(statusJson);

        Assert.NotNull(roundTrippedStatus);
        Assert.Equal(AutoTdpProtocol.BoostEfficient, roundTrippedStatus!.CpuBoostMode);
        Assert.True(roundTrippedStatus.AcBoostBursts);
        Assert.True(roundTrippedStatus.FreeSync);
    }

    [Theory]
    [InlineData("MEMCOMP=False", TweakState.Applied)]
    [InlineData("MEMCOMP=True", TweakState.NotApplied)]
    [InlineData("MEMCOMP=", TweakState.Unknown)]
    [InlineData("something-else", TweakState.Unknown)]
    public void CpuKernelTweaks_ParseMemoryCompressionState_maps_output_accurately(string line, TweakState expected)
    {
        Assert.Equal(expected, CpuKernelTweaks.ParseMemoryCompressionState([line]));
    }

    [Fact]
    public void CpuKernelTweaks_registry_and_powerplan_tweaks_have_expected_specs()
    {
        Assert.Equal(10, CpuKernelTweaks.MmcssGames.RegistryValues.Single(v => v.ValueName == "SystemResponsiveness").DesiredValue);
        Assert.Equal("High", CpuKernelTweaks.MmcssGames.RegistryValues.Single(v => v.ValueName == "Scheduling Category").DesiredValue);
        Assert.Equal(1, CpuKernelTweaks.GlobalTimerResolution.RegistryValues.Single().DesiredValue);
        Assert.Equal(67108864, CpuKernelTweaks.SvcHostMerge.RegistryValues.Single().DesiredValue);
        Assert.NotNull(CpuKernelTweaks.ApuEnergyPreference.ScriptDetect);
        Assert.NotNull(CpuKernelTweaks.AcPcieAspmOff.ScriptApply);
        Assert.NotNull(CpuKernelTweaks.AcCoreUnpark.ScriptRevert);
    }
}
