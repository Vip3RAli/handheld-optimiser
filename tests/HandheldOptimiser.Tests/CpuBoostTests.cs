using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>What the widget's CPU boost buttons write to the power plan, and how the state is read back.</summary>
public class CpuBoostTests
{
    private const uint Disabled = CpuBoostRules.Disabled;
    private const uint Aggressive = CpuBoostRules.Aggressive;
    private const uint EfficientAggressive = 4;

    [Fact]
    public void Off_disables_boost_plugged_in_and_on_battery() =>
        Assert.Equal(new BoostModes(Disabled, Disabled), CpuBoostRules.Target(false, new BoostModes(EfficientAggressive, Aggressive)));

    [Fact]
    public void On_puts_back_the_modes_from_before_it_was_turned_off() =>
        Assert.Equal(new BoostModes(EfficientAggressive, Aggressive), CpuBoostRules.Target(true, new BoostModes(EfficientAggressive, Aggressive)));

    [Fact]
    public void On_with_nothing_saved_is_aggressive() =>
        Assert.Equal(new BoostModes(Aggressive, Aggressive), CpuBoostRules.Target(true, new BoostModes(Disabled, Disabled)));

    [Fact]
    public void On_turns_boost_on_for_battery_too_when_the_plan_shipped_with_it_off() =>
        Assert.Equal(new BoostModes(EfficientAggressive, Aggressive), CpuBoostRules.Target(true, new BoostModes(EfficientAggressive, Disabled)));

    [Fact]
    public void Turning_off_saves_the_modes_as_they_are() =>
        Assert.Equal(new BoostModes(Aggressive, EfficientAggressive),
            CpuBoostRules.ToSave(new BoostModes(Aggressive, EfficientAggressive), new BoostModes(Disabled, Disabled)));

    [Fact]
    public void Turning_off_twice_keeps_what_was_saved_the_first_time() =>
        Assert.Equal(new BoostModes(EfficientAggressive, Aggressive),
            CpuBoostRules.ToSave(new BoostModes(Disabled, Disabled), new BoostModes(EfficientAggressive, Aggressive)));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void The_state_shown_is_the_one_for_the_power_source_in_use(bool onBattery, bool expected) =>
        Assert.Equal(expected, CpuBoostRules.IsOn(new BoostModes(Aggressive, Disabled), onBattery));
}
