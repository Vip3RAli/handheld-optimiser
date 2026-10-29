using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Left and right on a menu row's setting.</summary>
public sealed class SettingStepsTests
{
    [Fact]
    public void An_enum_setting_goes_round_at_either_end()
    {
        Assert.Equal(LibraryLayout.Row, SettingSteps.Step(LibraryLayout.Grid, 1));
        Assert.Equal(LibraryLayout.Grid, SettingSteps.Step(LibraryLayout.OldSchool, 1));
        Assert.Equal(LibraryLayout.OldSchool, SettingSteps.Step(LibraryLayout.Grid, -1));

        Assert.Equal(WindowsGameMode.XboxMode, SettingSteps.Step(WindowsGameMode.Off, 1));
        Assert.Equal(WindowsGameMode.Always, SettingSteps.Step(WindowsGameMode.XboxMode, 1));
        Assert.Equal(WindowsGameMode.Off, SettingSteps.Step(WindowsGameMode.Always, 1));
        Assert.Equal(WindowsGameMode.Always, SettingSteps.Step(WindowsGameMode.Off, -1));
    }

    [Fact]
    public void A_number_steps_among_its_choices_and_one_not_among_them_starts_at_the_first()
    {
        int[] rates = [0, 30, 40, 60];
        Assert.Equal(40, SettingSteps.StepAmong(30, rates, 1));
        Assert.Equal(0, SettingSteps.StepAmong(60, rates, 1));
        Assert.Equal(60, SettingSteps.StepAmong(0, rates, -1));
        Assert.Equal(30, SettingSteps.StepAmong(45, rates, 1));
    }

    [Fact]
    public void A_choice_starts_with_none_of_them()
    {
        int[] levels = [10, 20];
        Assert.Equal(10, SettingSteps.StepChoice<int>(null, levels, 1));
        Assert.Null(SettingSteps.StepChoice<int>(20, levels, 1));
        Assert.Equal(20, SettingSteps.StepChoice<int>(null, levels, -1));
        Assert.Null(SettingSteps.StepChoice<int>(10, levels, -1));
    }

    [Fact]
    public void A_switch_says_on_or_off()
    {
        Assert.Equal("On", SettingSteps.OnOff(true));
        Assert.Equal("Off", SettingSteps.OnOff(false));
    }
}
