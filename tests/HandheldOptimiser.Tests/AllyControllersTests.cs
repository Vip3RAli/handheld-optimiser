using HandheldOptimiser.PowerService;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Which controllers TDP Engine and the Replace ASUS Services switch take for an Ally.</summary>
public sealed class AllyControllersTests
{
    [Theory]
    [InlineData(@"\\?\hid#vid_0b05&pid_1abe&mi_02&col01#8&d1252f4&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData(@"\\?\hid#vid_0b05&pid_1b4c&mi_02&col01#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData(@"HID\VID_0B05&PID_1B4C&MI_05\8&1A2B3C4D&0&0000")]
    public void The_Ally_and_the_Ally_X_are_found(string path) => Assert.True(AllyControllers.Matches(path));

    [Theory]
    [InlineData(@"\\?\hid#vid_0b05&pid_1866&mi_02#8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData(@"\\?\hid#vid_045e&pid_1abe#8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_devices_are_not(string? path) => Assert.False(AllyControllers.Matches(path));

    [Fact]
    public void The_main_app_names_the_same_controllers_as_the_service() =>
        Assert.Equal(AllyControllers.ProductIds.Select(AllyControllers.HardwareId), ArmouryCrateTweaks.AllyControllers);

    [Fact]
    public void The_check_script_looks_for_every_controller()
    {
        var script = ArmouryCrateTweaks.BuildCheckScript();
        foreach (var id in ArmouryCrateTweaks.AllyControllers)
        {
            Assert.Contains($"'*{id}*'", script, StringComparison.Ordinal);
        }
    }
}
