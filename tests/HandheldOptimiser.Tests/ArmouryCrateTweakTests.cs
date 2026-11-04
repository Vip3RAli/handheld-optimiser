using System.IO;
using System.Runtime.CompilerServices;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The Replace Armoury Crate tweak and the narrow <see cref="SafetyGuard"/> exception it needs. Only the
/// lists, the guard and detection against a fake registry are tested: the apply and revert scripts change
/// real services, so they are never run here.
/// </summary>
public sealed class ArmouryCrateTweakTests
{
    [Fact]
    public void The_watchdog_stops_first_and_Armoury_Crate_last()
    {
        Assert.Equal("AsusAppService", ArmouryCrateTweaks.ServicesInStopOrder[0]);
        Assert.Equal("ArmouryCrateSEService", ArmouryCrateTweaks.ServicesInStopOrder[^1]);
    }

    [Fact]
    public void Every_service_it_changes_is_let_through_the_guard()
    {
        foreach (var name in ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices))
        {
            Assert.False(SafetyGuard.IsServiceProtected(name, out _), name);
        }
    }

    [Theory]
    [InlineData("AsusCertService")]
    [InlineData("ATKWMIACPIIO")]
    [InlineData("AsusSAIO")]
    [InlineData("ArmouryCrateService")]
    public void Other_ASUS_services_and_drivers_stay_protected(string name) =>
        Assert.True(SafetyGuard.IsServiceProtected(name, out _));

    [Fact]
    public void ASUS_registry_keys_stay_protected_even_for_the_named_services() =>
        Assert.True(SafetyGuard.IsRegistryPathProtected(RegistryRoot.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\AsusAppService", "Start", out _));

    [Fact]
    public void It_is_only_for_the_Ally_never_in_Apply_all_and_warns()
    {
        var tweak = ArmouryCrateTweaks.ReplaceArmouryCrate;
        Assert.Equal(HandheldDevice.RogAlly, tweak.OnlyFor);
        Assert.False(tweak.IncludeInOneClick);
        Assert.False(string.IsNullOrWhiteSpace(tweak.Warning));
        Assert.Equal(TweakCategory.FullScreen, tweak.Category);
    }

    [Fact]
    public async Task Detection_reads_Applied_only_when_replacing_and_every_service_is_disabled()
    {
        var registry = new FakeRegistry();
        var context = new TweakContext(TestLog.Instance, registry, new PowerShellRunner(TestLog.Instance));
        var detect = ArmouryCrateTweaks.ReplaceArmouryCrate.ScriptDetect!;

        Assert.Equal(TweakState.NotApplied, await detect(context, CancellationToken.None));

        registry.Set(RegistryRoot.LocalMachine, @"SOFTWARE\HandheldOptimiser\AllyControls", "Replacing", 1);
        foreach (var name in ArmouryCrateTweaks.ServicesInStopOrder)
        {
            registry.Set(RegistryRoot.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start", 4);
        }
        Assert.Equal(TweakState.Applied, await detect(context, CancellationToken.None));

        // An Armoury Crate update set one back to Automatic.
        registry.Set(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\ArmouryCrateSEService", "Start", 2);
        Assert.Equal(TweakState.Partial, await detect(context, CancellationToken.None));
    }

    /// <summary>A service ASUS did not install on this Ally is skipped rather than holding the switch at Partial.</summary>
    [Fact]
    public async Task A_missing_service_does_not_count_against_Applied()
    {
        var registry = new FakeRegistry();
        var context = new TweakContext(TestLog.Instance, registry, new PowerShellRunner(TestLog.Instance));
        var detect = ArmouryCrateTweaks.ReplaceArmouryCrate.ScriptDetect!;

        registry.Set(RegistryRoot.LocalMachine, @"SOFTWARE\HandheldOptimiser\AllyControls", "Replacing", 1);
        foreach (var name in ArmouryCrateTweaks.ServicesInStopOrder.Where(n => n != "GameSDK Service"))
        {
            registry.Set(RegistryRoot.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start", 4);
        }

        Assert.Equal(TweakState.Applied, await detect(context, CancellationToken.None));
    }

    [Fact]
    public async Task Replacing_without_the_services_disabled_reads_Partial_and_off_reads_NotApplied()
    {
        var registry = new FakeRegistry();
        var context = new TweakContext(TestLog.Instance, registry, new PowerShellRunner(TestLog.Instance));
        var detect = ArmouryCrateTweaks.ReplaceArmouryCrate.ScriptDetect!;

        foreach (var name in ArmouryCrateTweaks.ServicesInStopOrder)
        {
            registry.Set(RegistryRoot.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start", 4);
        }
        Assert.Equal(TweakState.NotApplied, await detect(context, CancellationToken.None));

        registry.Set(RegistryRoot.LocalMachine, @"SOFTWARE\HandheldOptimiser\AllyControls", "Replacing", 1);
        registry.Set(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AsusAppService", "Start", 2);
        Assert.Equal(TweakState.Partial, await detect(context, CancellationToken.None));
    }

    [Fact]
    public void The_scripts_quote_names_with_spaces_and_never_name_the_drivers()
    {
        var all = ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices).ToList();
        var apply = ArmouryCrateTweaks.BuildApplyScript(all, ArmouryCrateTweaks.ServicesInStopOrder, true, true);
        var revert = ArmouryCrateTweaks.BuildRevertScript(Journal("2", "0"));

        foreach (var script in new[] { apply, revert, ArmouryCrateTweaks.BuildCheckScript() })
        {
            Assert.DoesNotContain("ATKWMIACPIIO", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AsusSAIO", script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("'ROG Live Service'", apply, StringComparison.Ordinal);
        Assert.Contains("'GameSDK Service'", apply, StringComparison.Ordinal);
        Assert.Contains("Set-Start 'ROG Live Service' 'auto'", revert, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_puts_back_each_start_type_and_starts_Armoury_Crate_last()
    {
        var journal = Journal("3", "0");
        journal["service.AsusAppService.start"] = "2";
        journal["service.AsusAppService.delayed"] = "1";
        journal["service.ArmouryCrateSEService.start"] = "2";
        journal["service.LightingService.start"] = "4";

        var revert = ArmouryCrateTweaks.BuildRevertScript(journal);

        Assert.Contains("Set-Start 'AsusAppService' 'delayed-auto'", revert, StringComparison.Ordinal);
        Assert.Contains("Set-Start 'ASUSSwitch' 'demand'", revert, StringComparison.Ordinal);
        Assert.Contains("Set-Start 'LightingService' 'disabled'", revert, StringComparison.Ordinal);
        Assert.Contains("foreach ($n in @('AsusAppService', 'ArmouryCrateSEService'))", revert, StringComparison.Ordinal);
        Assert.Contains("Enable-ScheduledTask", revert, StringComparison.Ordinal);
        Assert.Contains("[byte[]]@(0x02, 0x00", revert, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_acts_only_on_its_own_names_and_removes_a_flag_that_was_not_there()
    {
        var revert = ArmouryCrateTweaks.BuildRevertScript(new Dictionary<string, string>
        {
            ["service.WinDefend.start"] = "4",
            ["task.framework"] = "Disabled",
            ["run.screenmanager"] = "absent"
        });

        Assert.DoesNotContain("WinDefend", revert, StringComparison.Ordinal);
        Assert.DoesNotContain("Enable-ScheduledTask", revert, StringComparison.Ordinal);
        Assert.Contains("DeleteValue('ROG Screen Manager', $false)", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service that will not stop or start, or a running process that will not close, has to fail the run:
    /// otherwise apply reads Applied while Armoury Crate still runs beside TDP Engine.
    /// </summary>
    [Fact]
    public void A_service_that_will_not_stop_or_start_is_counted_as_a_failure()
    {
        var all = ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices).ToList();
        var apply = Flat(ArmouryCrateTweaks.BuildApplyScript(all, ArmouryCrateTweaks.ServicesInStopOrder, true, true));
        var revert = Flat(ArmouryCrateTweaks.BuildRevertScript(Journal("2", "0")));

        Assert.Contains(
            "try { Stop-Service -Name $n -Force -ErrorAction Stop } catch { Write-Output \"  failed: $($_.Exception.Message)\"; $failed = $true }",
            apply, StringComparison.Ordinal);
        Assert.Contains("try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop }", apply, StringComparison.Ordinal);
        Assert.Contains(
            "if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Write-Output \"  failed: $($_.Exception.Message)\"; $failed = $true }",
            apply, StringComparison.Ordinal);
        Assert.DoesNotContain("ForEach-Object", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("-ErrorAction Continue", apply, StringComparison.Ordinal);
        Assert.EndsWith("if ($failed) { exit 1 }", apply, StringComparison.Ordinal);

        Assert.Contains(
            "try { Start-Service -Name $n -ErrorAction Stop } catch { Write-Output \"  failed: $($_.Exception.Message)\"; $script:failed = $true }",
            revert, StringComparison.Ordinal);
        Assert.DoesNotContain("-ErrorAction Continue", revert, StringComparison.Ordinal);
        Assert.EndsWith("if ($script:failed) { exit 1 }", revert, StringComparison.Ordinal);
    }

    /// <summary>
    /// TDP Engine ends with an error when it fails while running the buttons, and only failureflag 1 makes Windows
    /// start a service again after that, rather than only after a crash.
    /// </summary>
    [Fact]
    public void Apply_has_Windows_start_TDP_Engine_again_after_it_fails()
    {
        var apply = ArmouryCrateTweaks.BuildApplyScript(["AsusAppService"], ["AsusAppService"], false, false);

        Assert.Contains("sc.exe failure HandheldOptimiserAutoTdp reset= 86400 actions= restart/5000/restart/5000/restart/30000", apply, StringComparison.Ordinal);
        Assert.Contains("sc.exe failureflag HandheldOptimiserAutoTdp 1", apply, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="SafetyGuard"/> lets through exactly the services this tweak changes, so a name added to one list
    /// and not the other fails here rather than on a real Ally.
    /// </summary>
    [Fact]
    public void The_guard_allowlist_is_exactly_the_services_the_tweak_changes()
    {
        var changed = ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices);

        Assert.True(
            new HashSet<string>(SafetyGuard.ArmouryCrateReplacement, StringComparer.OrdinalIgnoreCase).SetEquals(changed),
            "SafetyGuard.ArmouryCrateReplacement and the tweak's lists differ.");
    }

    /// <summary>The script with each line trimmed and joined by one space, so a test does not depend on indentation.</summary>
    private static string Flat(string script) =>
        string.Join(" ", script.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>A journal with every listed service at one start type, the task Ready and the startup flag on.</summary>
    private static Dictionary<string, string> Journal(string start, string delayed)
    {
        var journal = new Dictionary<string, string>
        {
            ["task.framework"] = "Ready",
            ["run.screenmanager"] = "020000000000000000000000"
        };

        foreach (var name in ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices))
        {
            journal[$"service.{name}.start"] = start;
            journal[$"service.{name}.delayed"] = delayed;
        }

        return journal;
    }

    /// <summary>
    /// The player asked for no ASUS app to be removed that cannot be brought back: on 2026-10-19 turning the
    /// switch off could not bring the Command Center widget back. On 2026-10-28 they agreed to that one widget
    /// going, for their account only and only when Windows keeps a provisioned copy. Nothing else is removed,
    /// and the Apps page still cannot remove it.
    /// </summary>
    [Fact]
    public void Only_the_Command_Center_widget_is_removed_and_only_when_Windows_can_put_it_back()
    {
        var all = ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices).ToArray();
        var apply = ArmouryCrateTweaks.BuildApplyScript(all, ArmouryCrateTweaks.ServicesInStopOrder, true, true);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(apply, "Remove-AppxPackage"));
        Assert.Contains("Get-AppxProvisionedPackage -Online", apply, StringComparison.Ordinal);
        Assert.Contains("if ($kept.Count -gt 0)", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("-AllUsers", apply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Get-AppxPackage -Name '{ArmouryCrateTweaks.CommandCenterPackage}'", apply, StringComparison.Ordinal);
        Assert.True(SafetyGuard.IsAppxProtected(ArmouryCrateTweaks.CommandCenterPackage, out _));
    }

    [Fact]
    public void Undo_puts_the_widget_back_only_when_it_was_removed()
    {
        var removed = new Dictionary<string, string>(Journal("2", "0")) { ["widget.commandcenter"] = "removed" };

        Assert.Contains(
            $"Add-AppxPackage -RegisterByFamilyName -MainPackage '{ArmouryCrateTweaks.CommandCenterFamily}'",
            ArmouryCrateTweaks.BuildRevertScript(removed), StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage", ArmouryCrateTweaks.BuildRevertScript(Journal("2", "0")), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_sets_the_TDP_Engine_widget_as_the_Game_Bar_OEM_widget_and_revert_restores_Command_Center()
    {
        var all = ArmouryCrateTweaks.ServicesInStopOrder.Concat(ArmouryCrateTweaks.UpdateServices).ToArray();
        var apply = ArmouryCrateTweaks.BuildApplyScript(all, ArmouryCrateTweaks.ServicesInStopOrder, true, true);
        var revert = ArmouryCrateTweaks.BuildRevertScript(Journal("2", "0"));
        var revertAbsent = ArmouryCrateTweaks.BuildRevertScript(new Dictionary<string, string>(Journal("2", "0"))
        {
            ["widget.oem"] = "absent"
        });

        Assert.Contains(
            $"SetValue('{ArmouryCrateTweaks.OemWidgetValue}', '{ArmouryCrateTweaks.WidgetFamily}'",
            apply, StringComparison.Ordinal);
        Assert.Contains(
            $"SetValue('{ArmouryCrateTweaks.OemWidgetValue}', '{ArmouryCrateTweaks.CommandCenterFamily}'",
            revert, StringComparison.Ordinal);
        Assert.Contains(
            $"DeleteValue('{ArmouryCrateTweaks.OemWidgetValue}', $false)",
            revertAbsent, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_puts_back_the_OEM_widget_that_was_there_before()
    {
        var revert = ArmouryCrateTweaks.BuildRevertScript(new Dictionary<string, string>(Journal("2", "0"))
        {
            ["widget.oem"] = "Some.OtherMaker_abc123"
        });

        Assert.Contains($"SetValue('{ArmouryCrateTweaks.OemWidgetValue}', 'Some.OtherMaker_abc123'", revert, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Game_Bar_widget_manifest_requests_Compact_Mode_priority_and_device_widget_placement()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "Package.appxmanifest"));

        Assert.Contains("<FavoriteAfterInstall>true</FavoriteAfterInstall>", manifest, StringComparison.Ordinal);
        Assert.Contains("<CompactModePriorityPlacement>true</CompactModePriorityPlacement>", manifest, StringComparison.Ordinal);
        Assert.Contains("<IsDeviceWidget>true</IsDeviceWidget>", manifest, StringComparison.Ordinal);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void The_tweak_is_in_the_catalog() =>
        Assert.Contains(TweakCatalog.All, t => t.Id == "asus.replace-armoury-crate");
}
