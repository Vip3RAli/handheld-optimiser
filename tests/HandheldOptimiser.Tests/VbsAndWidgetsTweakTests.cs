using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Microsoft.Win32;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Two tweaks that reported success, or tried to, while Windows carried on as before. On a ROG Ally,
/// VBS kept starting until Windows Hello's two protected sign-in scenarios were switched off, and
/// Windows refuses the Widgets policy value to every program but its own.
/// </summary>
public sealed class VbsAndWidgetsTweakTests
{
    private const string Scenarios = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios";

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity")]
    [InlineData(Scenarios + @"\WindowsHello", "Enabled")]
    [InlineData(Scenarios + @"\SecureBiometrics", "Enabled")]
    public void The_VBS_tweak_switches_off_everything_that_asks_Windows_for_VBS(string subKey, string valueName)
    {
        var spec = Assert.Single(
            GamingTweaks.VirtualisationPlatform.RegistryValues,
            s => s.Root == RegistryRoot.LocalMachine && s.SubKey == subKey && s.ValueName == valueName);

        Assert.Equal(0, spec.DesiredValue);
        Assert.Equal(RegistryValueKind.DWord, spec.Kind);
    }

    [Fact]
    public void The_VBS_tweak_warns_about_the_PIN_and_the_fingerprint()
    {
        var warning = GamingTweaks.VirtualisationPlatform.Warning;

        Assert.NotNull(warning);
        Assert.Contains("PIN", warning);
        Assert.Contains("fingerprint", warning);
    }

    [Fact]
    public void The_boot_hypervisor_setting_does_not_decide_the_VBS_tweaks_state()
    {
        // What the Ally reported after a Windows update had wiped the setting: the tweak then read
        // "Partly applied", though the setting neither starts nor stops VBS.
        string[] lines =
        [
            "FEATURE=VirtualMachinePlatform=Disabled",
            "FEATURE=HypervisorPlatform=Disabled",
            "BCD=absent"
        ];

        Assert.Equal(TweakState.Applied, GamingTweaks.VirtualisationFeaturesState(lines));
    }

    [Fact]
    public void A_virtualisation_feature_left_on_means_the_VBS_tweak_is_not_applied()
    {
        string[] lines = ["FEATURE=VirtualMachinePlatform=Enabled", "FEATURE=HypervisorPlatform=Disabled"];

        Assert.Equal(TweakState.NotApplied, GamingTweaks.VirtualisationFeaturesState(lines));
    }

    [Fact]
    public void No_answer_about_the_virtualisation_features_is_unknown()
    {
        Assert.Equal(TweakState.Unknown, GamingTweaks.VirtualisationFeaturesState([]));
        Assert.Equal(TweakState.Unknown, GamingTweaks.VirtualisationFeaturesState(["BCD=Off"]));
    }

    [Theory]
    [InlineData("0", HealthStatus.Good, "Off")]
    [InlineData("1", HealthStatus.Warning, "Switched on, not running")]
    [InlineData("2", HealthStatus.Warning, "Running")]
    public void The_health_check_reports_what_Windows_says_about_VBS(string status, HealthStatus expected, string detail)
    {
        var check = SystemStateService.VbsCheck(status);

        Assert.Equal(expected, check.Status);
        Assert.Equal(detail, check.Detail);
        Assert.Equal(expected == HealthStatus.Good, check.Advice is null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7")]
    public void The_health_check_does_not_guess_at_VBS(string? status) =>
        Assert.Equal(HealthStatus.Unknown, SystemStateService.VbsCheck(status).Status);

    [Fact]
    public void Turn_off_Widgets_no_longer_writes_the_policy_Windows_refuses() =>
        Assert.Empty(DebloatTweaks.Widgets.RegistryValues);

    [Fact]
    public void Turn_off_Widgets_can_still_undo_the_policy_an_older_version_wrote()
    {
        var written = new RegistryValueSnapshot
        {
            Root = RegistryRoot.LocalMachine,
            SubKey = @"SOFTWARE\Policies\Microsoft\Dsh",
            ValueName = "AllowNewsAndInterests",
            KeyExisted = false,
            ValueExisted = false
        };

        Assert.True(DebloatTweaks.Widgets.OwnsRegistryValue(written));
    }

    [Fact]
    public void Widgets_are_off_when_their_app_is_not_installed()
    {
        Assert.Equal(TweakState.Applied, DebloatTweaks.WidgetsState(["WIDGETS=Absent"]));
        Assert.Equal(TweakState.NotApplied, DebloatTweaks.WidgetsState(["WIDGETS=Installed"]));
    }

    [Fact]
    public void No_answer_about_the_Widgets_app_is_unknown()
    {
        Assert.Equal(TweakState.Unknown, DebloatTweaks.WidgetsState([]));
        Assert.Equal(TweakState.Unknown, DebloatTweaks.WidgetsState(["WIDGETS=Maybe"]));
    }

    [Fact]
    public void The_Widgets_app_is_one_the_guard_lets_go() =>
        Assert.False(SafetyGuard.IsAppxProtected(DebloatTweaks.WidgetsPackage, out _));

    /// <summary>The Widgets app shares the start of its name with parts of the Windows shell.</summary>
    [Theory]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("MicrosoftWindows.Client.Core")]
    [InlineData("MicrosoftWindows.Client.WebExperienceHost")]
    [InlineData("MicrosoftWindows.Client.WebExperience.Extra")]
    public void Only_the_Widgets_app_itself_is_let_go(string identityName) =>
        Assert.True(SafetyGuard.IsAppxProtected(identityName, out _));
}
