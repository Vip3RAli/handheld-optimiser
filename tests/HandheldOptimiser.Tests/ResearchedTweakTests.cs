using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Microsoft.Win32;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The tweaks added from the October 2026 research: the power button, the pop-ups, Windows AI and
/// small crash dumps. What they write, that the safety guard lets it through (it vetoes at run time,
/// so a refusal would otherwise only show on a real device), and that each can be undone.
/// </summary>
public sealed class ResearchedTweakTests
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private const string AiPolicy = @"Software\Policies\Microsoft\Windows\WindowsAI";
    private const string AiServiceKey = @"SYSTEM\CurrentControlSet\Services\WSAIFabricSvc";

    private readonly FakeRegistry _registry = new();
    private readonly FakeJournal _journal = new();
    private readonly TweakEngine _engine;

    public ResearchedTweakTests()
    {
        var log = TestLog.Instance;
        var runner = new PowerShellRunner(log);

        // Never called: every apply here passes createRestorePoint: false.
        var restorePoints = new RestorePointService(log, runner, new RegistryHelper(log));

        _engine = new TweakEngine(log, _registry, runner, restorePoints, _journal, HandheldDevice.RogAlly);
    }

    [Theory]
    [InlineData(CurrentVersion + @"\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled")]
    [InlineData(CurrentVersion + @"\Notifications\Settings\Windows.SystemToast.BackupReminder", "Enabled")]
    [InlineData(CurrentVersion + @"\Mobility", "OptedIn")]
    [InlineData(CurrentVersion + @"\SystemSettings\AccountNotifications", "EnableAccountNotifications")]
    [InlineData(CurrentVersion + @"\ContentDeliveryManager", "SoftLandingEnabled")]
    public void Hide_ads_also_switches_off_the_pop_ups(string subKey, string valueName)
    {
        var spec = Assert.Single(
            DebloatTweaks.ShellAds.RegistryValues,
            s => s.Root == RegistryRoot.CurrentUser && s.SubKey == subKey && s.ValueName == valueName);

        Assert.Equal(0, spec.DesiredValue);
        Assert.Equal(RegistryValueKind.DWord, spec.Kind);
    }

    [Theory]
    [InlineData(RegistryRoot.LocalMachine, "AllowRecallEnablement", 0)]
    [InlineData(RegistryRoot.LocalMachine, "TurnOffSavingSnapshots", 1)]
    [InlineData(RegistryRoot.LocalMachine, "DisableAIDataAnalysis", 1)]
    [InlineData(RegistryRoot.LocalMachine, "DisableClickToDo", 1)]
    [InlineData(RegistryRoot.CurrentUser, "DisableAIDataAnalysis", 1)]
    [InlineData(RegistryRoot.CurrentUser, "DisableClickToDo", 1)]
    public void The_Windows_AI_tweak_writes_the_documented_policies(RegistryRoot root, string valueName, int value)
    {
        var spec = Assert.Single(
            DebloatTweaks.WindowsAi.RegistryValues,
            s => s.Root == root && s.SubKey == AiPolicy && s.ValueName == valueName);

        Assert.Equal(value, spec.DesiredValue);
    }

    [Fact]
    public void The_Windows_AI_tweak_warns_that_Recall_pictures_are_deleted_and_is_not_one_click()
    {
        Assert.Contains("deleted", DebloatTweaks.WindowsAi.Warning);
        Assert.False(DebloatTweaks.WindowsAi.IncludeInOneClick);
    }

    [Theory]
    [InlineData(2, TweakState.Applied, TweakState.NotApplied)]
    [InlineData(3, TweakState.Applied, TweakState.Applied)]
    [InlineData(4, TweakState.Partial, TweakState.Partial)]
    [InlineData(null, TweakState.Applied, TweakState.Applied)]
    [InlineData(null, TweakState.NotApplied, TweakState.NotApplied)]
    public void Only_an_AI_service_that_starts_by_itself_counts_against_the_tweak(
        int? startType, TweakState policies, TweakState expected)
    {
        Assert.Equal(expected, DebloatTweaks.AiServiceState(startType, policies));
    }

    [Fact]
    public async Task On_a_handheld_with_no_AI_service_the_tweak_applies_and_reverts_through_the_registry_alone()
    {
        var tweak = DebloatTweaks.WindowsAi;

        Assert.Equal(TweakState.NotApplied, await _engine.DetectAsync(tweak));

        var summary = await _engine.ApplyAsync([tweak], "test", createRestorePoint: false);

        Assert.Equal(0, summary.Failures);
        Assert.Equal(TweakState.Applied, await _engine.DetectAsync(tweak));
        Assert.Equal(6, Assert.Single(_journal.Entries).RegistrySnapshots.Count);
        Assert.Null(_registry.ReadValue(RegistryRoot.LocalMachine, AiServiceKey, "Start"));

        var reverted = await _engine.RevertAsync(tweak);

        Assert.False(reverted.IsFailure);
        Assert.Equal(TweakState.NotApplied, await _engine.DetectAsync(tweak));
        Assert.Null(_registry.ReadValue(RegistryRoot.LocalMachine, AiPolicy, "AllowRecallEnablement"));
    }

    [Fact]
    public async Task An_AI_service_another_tool_disabled_is_left_as_it_is()
    {
        // The development Ally: start type 4, set by some other tool. Start on demand would loosen it.
        _registry.Set(RegistryRoot.LocalMachine, AiServiceKey, "Start", 4);

        var summary = await _engine.ApplyAsync([DebloatTweaks.WindowsAi], "test", createRestorePoint: false);

        Assert.Equal(0, summary.Failures);
        Assert.Empty(Assert.Single(_journal.Entries).CapturedState);
        Assert.Equal(TweakState.Applied, await _engine.DetectAsync(DebloatTweaks.WindowsAi));
    }

    [Fact]
    public async Task An_AI_service_that_starts_by_itself_leaves_the_tweak_partly_applied_until_it_is_changed()
    {
        _registry.Set(RegistryRoot.LocalMachine, AiServiceKey, "Start", 2);

        foreach (var spec in DebloatTweaks.WindowsAi.RegistryValues)
        {
            _registry.Set(spec.Root, spec.SubKey, spec.ValueName, spec.DesiredValue);
        }

        Assert.Equal(TweakState.Partial, await _engine.DetectAsync(DebloatTweaks.WindowsAi));
    }

    [Fact]
    public void Small_crash_dumps_is_one_value_and_not_one_click()
    {
        var tweak = StorageTweaks.SmallCrashDumps;
        var spec = Assert.Single(tweak.RegistryValues);

        Assert.Equal(RegistryRoot.LocalMachine, spec.Root);
        Assert.Equal(@"SYSTEM\CurrentControlSet\Control\CrashControl", spec.SubKey);
        Assert.Equal("CrashDumpEnabled", spec.ValueName);
        Assert.Equal(3, spec.DesiredValue);
        Assert.True(tweak.RequiresReboot);
        Assert.False(tweak.IncludeInOneClick);
    }

    [Fact]
    public void The_power_button_tweak_is_offered_with_a_warning_and_not_one_click()
    {
        var tweak = Assert.Single(SleepTweaks.All, t => t.Id == "sleep.powerbutton");

        Assert.Equal(TweakCategory.Sleep, tweak.Category);
        Assert.False(tweak.IncludeInOneClick);
        Assert.Contains("10 to 20 seconds", tweak.Warning);
        Assert.Contains("On battery", tweak.Description);
    }

    public static TheoryData<string> NewTweakIds => ["debloat.shell_ads", "debloat.windows_ai", "storage.crashdump"];

    [Theory]
    [MemberData(nameof(NewTweakIds))]
    public void The_safety_guard_lets_every_value_through(string tweakId)
    {
        var tweak = _engine.FindById(tweakId);
        Assert.NotNull(tweak);

        foreach (var spec in tweak.RegistryValues)
        {
            Assert.False(SafetyGuard.IsRegistryPathProtected(spec, out var reason), $"{spec.DisplayPath}: {reason}");
        }
    }

    [Fact]
    public void The_safety_guard_lets_the_AI_service_be_changed()
    {
        Assert.False(SafetyGuard.IsServiceProtected(DebloatTweaks.AiService, out var reason), reason);
    }

    [Fact]
    public void Every_tweak_still_has_its_own_id()
    {
        var duplicates = _engine.AllTweaks.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key);

        Assert.Empty(duplicates);
    }
}
