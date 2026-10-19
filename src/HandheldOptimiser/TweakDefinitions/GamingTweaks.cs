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
    private const string HelloKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\WindowsHello";
    private const string SecureBiometricsKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\SecureBiometrics";

    private static readonly string[] VirtualisationFeatures = ["VirtualMachinePlatform", "HypervisorPlatform"];

    /// <summary>The values bcdedit accepts for hypervisorlaunchtype.</summary>
    private static readonly string[] HypervisorLaunchTypes = ["Off", "Auto"];

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
    /// mitigation, hence the explicit warning rather than a silent toggle.
    /// </summary>
    public static Tweak MemoryIntegrity => new()
    {
        Id = "gaming.hvci",
        Name = "Turn off Memory Integrity",
        Description =
            "Turns off Memory Integrity (also called Core Isolation or HVCI), which checks drivers for " +
            "tampering. Can raise frame rates in CPU-heavy games.",
        Category = TweakCategory.Games,
        Risk = RiskLevel.SecurityTradeoff,
        RequiresReboot = true,
        Warning =
            "Lowers protection against malicious or vulnerable drivers. Defender, the firewall and " +
            "Windows Update keep working. Fine for a dedicated gaming handheld; not recommended if it " +
            "handles work email or banking.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, HvciKey, "Enabled", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Virtual Machine Platform plus everything that asks Windows to start VBS. Disabling these removes
    /// the VBS overhead that sits under every process, at the cost of WSL2, Sandbox, the Android
    /// subsystem and Windows Hello's protected sign-in.
    ///
    /// The two sign-in scenarios are what decide it. On a ROG Ally, with a fingerprint reader, Windows
    /// kept starting VBS "due to VBS registry configuration" on every boot with the main switch at 0,
    /// both features off and the boot hypervisor off, and stopped on the first boot with both
    /// scenarios at 0. Either one alone is not known to be enough, so both are written.
    /// </summary>
    public static Tweak VirtualisationPlatform => new()
    {
        Id = "gaming.vmp",
        Name = "Turn off Virtual Machine Platform and VBS",
        Description =
            "Turns off virtualisation-based security (VBS). Virtual Machine Platform, Windows " +
            "Hypervisor Platform and Windows Hello's protected sign-in can keep VBS running, so this " +
            "turns those off too. Removes the VBS overhead from every process.",
        Category = TweakCategory.Games,
        Risk = RiskLevel.Breaking,
        RequiresReboot = true,
        // Opt-in only: Vanguard and FACEIT games stop launching, which is too easy to miss in a one-click run.
        IncludeInOneClick = false,
        Warning =
            "If Windows Hello is protected by it, your PIN has to be reset at the next sign-in, which " +
            "needs your account password, and your fingerprint or face set up again without that " +
            "protection. WSL, Windows Sandbox, the Windows Subsystem for Android, Hyper-V and Credential " +
            "Guard stop working until you undo this tweak. Some anti-cheat (notably Vanguard and Faceit) needs " +
            "virtualisation and will refuse to launch.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, DeviceGuardKey, "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, HelloKey, "Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, SecureBiometricsKey, "Enabled", 0, RegistryValueKind.DWord)
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
                """,
                "Check virtualisation platform state",
                ct,
                echoScript: false);

            return VirtualisationFeaturesState(outcome.OutputLines);
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
                $failed = $false
                foreach ($f in 'VirtualMachinePlatform','HypervisorPlatform') {
                    $state = (Get-WindowsOptionalFeature -Online -FeatureName $f -ErrorAction SilentlyContinue).State
                    if ($state -eq 'Enabled') {
                        Write-Output "Disabling optional feature: $f"
                        try { Disable-WindowsOptionalFeature -Online -FeatureName $f -NoRestart -ErrorAction Stop | Out-Null }
                        catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                    } else {
                        Write-Output "Optional feature already disabled: $f"
                    }
                }
                Write-Output 'Setting boot hypervisor launch type to off'
                & bcdedit /set '{current}' hypervisorlaunchtype off
                if ($LASTEXITCODE -ne 0) { Write-Output "bcdedit failed (exit $LASTEXITCODE)"; $failed = $true }
                if ($failed) { exit 1 }
                """,
                "Disable Virtual Machine Platform and boot hypervisor",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("gaming.vmp", "Virtualisation platform disabled.", rebootRequired: true)
                : TweakResult.Fail("gaming.vmp", "DISM or bcdedit reported a failure. The console has the detail.");
        },
        ScriptRevert = async (ctx, journal, ct) =>
        {
            // The journal is a file on disk that ends up in an elevated script, so only names and values
            // this tweak could have written are used from it.
            var toEnable = VirtualisationFeatures
                .Where(f => journal.CapturedState.TryGetValue($"feature.{f}", out var s) &&
                            s.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var launchType = journal.CapturedState.TryGetValue("bcd.hypervisorlaunchtype", out var lt) &&
                             HypervisorLaunchTypes.FirstOrDefault(t => t.Equals(lt, StringComparison.OrdinalIgnoreCase)) is { } known
                ? known
                : "auto";

            var featureList = string.Join(",", toEnable.Select(f => $"'{f}'"));

            var script = $$"""
                $ErrorActionPreference = 'Continue'
                $failed = $false
                $features = @({{featureList}})
                if ($features.Count -eq 0) {
                    Write-Output 'No optional features were enabled before; only restoring boot configuration.'
                }
                foreach ($f in $features) {
                    Write-Output "Re-enabling optional feature: $f"
                    try { Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart -ErrorAction Stop | Out-Null }
                    catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                }
                & bcdedit /set '{current}' hypervisorlaunchtype {{launchType}}
                if ($LASTEXITCODE -ne 0) { Write-Output "bcdedit failed (exit $LASTEXITCODE)"; $failed = $true }
                if ($failed) { exit 1 }
                """;

            var outcome = await ctx.Runner.RunScriptAsync(script, "Restore virtualisation platform", ct);

            return outcome.Succeeded
                ? TweakResult.Ok("gaming.vmp", "Virtualisation platform restored.", rebootRequired: true)
                : TweakResult.Fail("gaming.vmp", "Could not fully restore virtualisation platform. The console has the detail.");
        }
    };

    /// <summary>
    /// Kills background capture only. The Xbox app, Game Pass and the overlay itself are untouched, which
    /// is what keeps Game Pass installs and cloud saves working.
    /// </summary>
    public static Tweak GameDvr => new()
    {
        Id = "gaming.gamedvr",
        Name = "Turn off background game recording",
        Description =
            "Stops the Game Bar recording gameplay in the background. The Xbox app, Game Pass, cloud " +
            "saves and the overlay keep working.",
        Category = TweakCategory.Games,
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
            "Removes the delay of about ten seconds Windows puts on startup apps, so Armoury Crate and " +
            "the desktop are ready sooner after boot.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
                "StartupDelayInMSec", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// The script half of the VBS tweak's state, from the FEATURE= lines its check prints: applied when
    /// every virtualisation feature is off. The boot hypervisor setting is left out on purpose. Apply
    /// still switches it off, but it neither starts nor stops VBS, and a Windows update removes it,
    /// which made a tweak that was fully in place read as partly applied.
    /// </summary>
    public static TweakState VirtualisationFeaturesState(IEnumerable<string> outputLines)
    {
        var states = outputLines
            .Where(l => l.StartsWith("FEATURE=", StringComparison.Ordinal))
            .Select(l => l.Split('=', 3))
            .Where(p => p.Length == 3)
            .Select(p => p[2])
            .ToList();

        if (states.Count == 0)
        {
            return TweakState.Unknown;
        }

        return states.All(s => s.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            ? TweakState.Applied
            : TweakState.NotApplied;
    }

    private static bool SafetyGuardCheck(string feature, TweakContext ctx, out TweakResult? blocked)
    {
        if (Services.SafetyGuard.IsOptionalFeatureProtected(feature, out var reason))
        {
            ctx.Log.Error($"BLOCKED optional feature {feature}: {reason}");
            blocked = TweakResult.Blocked("gaming.vmp", reason!);
            return true;
        }

        blocked = null;
        return false;
    }
}
