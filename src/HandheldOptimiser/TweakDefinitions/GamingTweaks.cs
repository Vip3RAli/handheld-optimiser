using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Handheld and gaming tweaks. These are the ones that actually move the frame rate on a Z1 Extreme,
/// mostly by getting the hypervisor out of the way of the CPU.
/// </summary>
public static class GamingTweaks
{
    private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    public static IReadOnlyList<Tweak> All =>
    [
        MemoryIntegrity,
        VirtualisationPlatform,
        GameDvr,
        StartupDelay
    ];

    /// <summary>
    /// HVCI runs driver code verification inside the hypervisor. Turning it off is usually the single
    /// biggest CPU-bound win on a handheld, and it is also a genuine reduction in kernel exploit
    /// mitigation — hence the explicit warning rather than a silent toggle.
    /// </summary>
    public static Tweak MemoryIntegrity => new()
    {
        Id = "gaming.hvci",
        Name = "Disable Memory Integrity (Core Isolation / HVCI)",
        Description =
            "Stops hypervisor-enforced code integrity checks on driver code. Typically the largest single " +
            "CPU-bound frame rate gain on the Ally, and reduces frame time spikes in CPU-heavy games.",
        Category = TweakCategory.Gaming,
        Risk = RiskLevel.SecurityTradeoff,
        RequiresReboot = true,
        Warning =
            "This lowers your machine's defence against malicious or vulnerable kernel drivers. Windows " +
            "Defender, the firewall and Windows Update all keep working normally, but driver-level exploits " +
            "become easier. Recommended for a dedicated gaming handheld; not recommended if this device " +
            "handles work email or banking.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, HvciKey, "Enabled", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Virtual Machine Platform plus the boot-time hypervisor. Disabling these removes the VBS overhead
    /// that sits under every process, at the cost of WSL2, Sandbox and the Android subsystem.
    /// </summary>
    public static Tweak VirtualisationPlatform => new()
    {
        Id = "gaming.vmp",
        Name = "Disable Virtual Machine Platform & VBS",
        Description =
            "Turns off Virtual Machine Platform, the Windows Hypervisor Platform and the boot hypervisor, " +
            "removing virtualisation-based security overhead from every process on the system.",
        Category = TweakCategory.Gaming,
        Risk = RiskLevel.Breaking,
        RequiresReboot = true,
        Warning =
            "WSL / WSL2, Windows Sandbox, the Windows Subsystem for Android, Hyper-V VMs and Credential " +
            "Guard will all stop working until this is reverted. Some anti-cheat systems (notably Vanguard " +
            "and Faceit) require virtualisation and will refuse to launch. Revert this tweak if you need any " +
            "of those.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, DeviceGuardKey, "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord)
        ],
        ScriptDetect = async (ctx, ct) =>
        {
            var outcome = await ctx.Runner.RunScriptAsync(
                """
                $ErrorActionPreference = 'SilentlyContinue'
                foreach ($f in 'VirtualMachinePlatform','HypervisorPlatform') {
                    $s = (Get-WindowsOptionalFeature -Online -FeatureName $f).State
                    Write-Output "FEATURE=$f=$s"
                }
                $line = bcdedit /enum '{current}' | Select-String -Pattern 'hypervisorlaunchtype'
                if ($line) { Write-Output "BCD=$($line.ToString().Trim())" } else { Write-Output 'BCD=absent' }
                """,
                "Check virtualisation platform state",
                ct,
                echoScript: false);

            var features = outcome.OutputLines
                .Where(l => l.StartsWith("FEATURE=", StringComparison.Ordinal))
                .Select(l => l.Split('=', 3))
                .Where(p => p.Length == 3)
                .ToDictionary(p => p[1], p => p[2], StringComparer.OrdinalIgnoreCase);

            var bcdLine = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("BCD=", StringComparison.Ordinal)) ?? string.Empty;
            var hypervisorOff = bcdLine.Contains("off", StringComparison.OrdinalIgnoreCase);

            var featuresDisabled = features.Count > 0 &&
                                   features.Values.All(v => v.Equals("Disabled", StringComparison.OrdinalIgnoreCase));

            if (features.Count == 0)
            {
                return TweakState.Unknown;
            }

            return featuresDisabled && hypervisorOff ? TweakState.Applied
                : !featuresDisabled && !hypervisorOff ? TweakState.NotApplied
                : TweakState.Partial;
        },
        ScriptApply = async (ctx, journal, ct) =>
        {
            foreach (var feature in new[] { "VirtualMachinePlatform", "HypervisorPlatform" })
            {
                if (SafetyGuardCheck(feature, ctx, out var blocked))
                {
                    return blocked!;
                }
            }

            // Record what was on before so revert restores the original mix rather than blindly
            // enabling both features on a machine that only ever had one.
            var capture = await ctx.Runner.RunScriptAsync(
                """
                $ErrorActionPreference = 'SilentlyContinue'
                foreach ($f in 'VirtualMachinePlatform','HypervisorPlatform') {
                    $s = (Get-WindowsOptionalFeature -Online -FeatureName $f).State
                    Write-Output "FEATURE=$f=$s"
                }
                $line = bcdedit /enum '{current}' | Select-String -Pattern 'hypervisorlaunchtype'
                if ($line) {
                    Write-Output "BCD=$(($line.ToString().Trim() -split '\s+')[-1])"
                } else {
                    Write-Output 'BCD=absent'
                }
                """,
                "Capture virtualisation state before changing it",
                ct,
                echoScript: false);

            foreach (var line in capture.OutputLines)
            {
                if (line.StartsWith("FEATURE=", StringComparison.Ordinal))
                {
                    var parts = line.Split('=', 3);
                    if (parts.Length == 3)
                    {
                        journal.CapturedState[$"feature.{parts[1]}"] = parts[2];
                    }
                }
                else if (line.StartsWith("BCD=", StringComparison.Ordinal))
                {
                    journal.CapturedState["bcd.hypervisorlaunchtype"] = line[4..];
                }
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                """
                $ErrorActionPreference = 'Continue'
                foreach ($f in 'VirtualMachinePlatform','HypervisorPlatform') {
                    $state = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue).State
                    if ($state -eq 'Enabled') {
                        Write-Output "Disabling optional feature: $f"
                        Disable-WindowsOptionalFeature -Online -FeatureName $f -NoRestart -ErrorAction Continue | Out-Null
                    } else {
                        Write-Output "Optional feature already disabled: $f"
                    }
                }
                Write-Output 'Setting boot hypervisor launch type to off'
                & bcdedit /set '{current}' hypervisorlaunchtype off
                """,
                "Disable Virtual Machine Platform and boot hypervisor",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("gaming.vmp", "Virtualisation platform disabled.", rebootRequired: true)
                : TweakResult.Fail("gaming.vmp", "DISM or bcdedit reported a failure. See log.");
        },
        ScriptRevert = async (ctx, journal, ct) =>
        {
            var toEnable = journal.CapturedState
                .Where(kv => kv.Key.StartsWith("feature.", StringComparison.Ordinal) &&
                             kv.Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key["feature.".Length..])
                .ToList();

            var launchType = journal.CapturedState.TryGetValue("bcd.hypervisorlaunchtype", out var lt) && lt != "absent"
                ? lt
                : "auto";

            var featureList = toEnable.Count > 0
                ? string.Join(",", toEnable.Select(f => $"'{f}'"))
                : null;

            var script = featureList is null
                ? $$"""
                   $ErrorActionPreference = 'Continue'
                   Write-Output 'No optional features were enabled before; only restoring boot configuration.'
                   & bcdedit /set '{current}' hypervisorlaunchtype {{launchType}}
                   """
                : $$"""
                   $ErrorActionPreference = 'Continue'
                   foreach ($f in {{featureList}}) {
                       Write-Output "Re-enabling optional feature: $f"
                       Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart -ErrorAction Continue | Out-Null
                   }
                   & bcdedit /set '{current}' hypervisorlaunchtype {{launchType}}
                   """;

            var outcome = await ctx.Runner.RunScriptAsync(script, "Restore virtualisation platform", ct);

            return outcome.Succeeded
                ? TweakResult.Ok("gaming.vmp", "Virtualisation platform restored.", rebootRequired: true)
                : TweakResult.Fail("gaming.vmp", "Could not fully restore virtualisation platform. See log.");
        }
    };

    /// <summary>
    /// Kills background capture only. The Xbox app, Game Pass and the overlay itself are untouched, which
    /// is what keeps Game Pass installs and cloud saves working.
    /// </summary>
    public static Tweak GameDvr => new()
    {
        Id = "gaming.gamedvr",
        Name = "Disable Game DVR & background recording",
        Description =
            "Stops the Game Bar from continuously recording gameplay in the background. The Xbox app, " +
            "Game Pass, cloud saves and the overlay itself keep working — only the capture pipeline stops.",
        Category = TweakCategory.Gaming,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0, RegistryValueKind.DWord)
        ]
    };

    public static Tweak StartupDelay => new()
    {
        Id = "gaming.startupdelay",
        Name = "Remove Windows startup delay",
        Description =
            "Windows holds startup apps back by about ten seconds after sign-in to keep the desktop " +
            "responsive. Removing the delay gets Armoury Crate and the desktop usable sooner after boot.",
        Category = TweakCategory.Gaming,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
                "StartupDelayInMSec", 0, RegistryValueKind.DWord)
        ]
    };

    private static bool SafetyGuardCheck(string feature, TweakContext ctx, out TweakResult? blocked)
    {
        if (Services.SafetyGuard.IsOptionalFeatureProtected(feature, out var reason))
        {
            ctx.Log.Error($"BLOCKED optional feature {feature} — {reason}");
            blocked = TweakResult.Blocked("gaming.vmp", reason!);
            return true;
        }

        blocked = null;
        return false;
    }
}
