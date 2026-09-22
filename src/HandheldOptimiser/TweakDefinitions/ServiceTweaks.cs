using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Services that are safe to switch off on a gaming handheld. Every service name passes through
/// <see cref="SafetyGuard.IsServiceProtected"/> before anything is changed.
///
/// Start types are changed with sc.exe rather than by writing the registry directly: the Service Control
/// Manager caches configuration at boot, so a raw registry edit only takes effect after a restart, and a
/// revert done that way could not start the service again until then either.
/// </summary>
public static class ServiceTweaks
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public static IReadOnlyList<Tweak> All =>
    [
        SysMain,
        SearchIndexer,
        PrintAndFax,
        CompatibilityAssistant,
        LinkTracking,
        UnusedHandheldServices
    ];

    public static Tweak CompatibilityAssistant => DisableServices(
        id: "services.pcasvc",
        name: "Disable Program Compatibility Assistant",
        description:
            "Stops the service that watches every program launch for known compatibility problems. It runs " +
            "constantly and has no role in games, which ship their own compatibility handling.",
        risk: RiskLevel.Safe,
        warning: null,
        includeInDragCar: true,
        services: ["PcaSvc"]);

    public static Tweak LinkTracking => DisableServices(
        id: "services.trkwks",
        name: "Disable Distributed Link Tracking",
        description:
            "Stops the service that repairs shortcuts when their target moves to another NTFS drive or " +
            "network share. Nothing on a single-drive handheld relies on it.",
        risk: RiskLevel.Safe,
        warning: null,
        includeInDragCar: true,
        services: ["TrkWks"]);

    /// <summary>
    /// All of these are Manual and stopped on a stock Ally, so disabling them frees nothing at runtime.
    /// The value is that nothing can start them later. Kept as one toggle and out of Drag Car Mode so it is
    /// not mistaken for a performance change.
    /// </summary>
    public static Tweak UnusedHandheldServices => DisableServices(
        id: "services.unused",
        name: "Disable unused handheld services",
        description:
            "Retail Demo, Wallet, Payments & NFC, Phone Service, Downloaded Maps Manager, Internet Connection " +
            "Sharing, Mobile Hotspot, Windows Insider, Parental Controls, Media Player Network Sharing and the " +
            "ActiveX Installer. These normally sit stopped, so this is tidying rather than a performance gain: " +
            "it just stops anything waking them up.",
        risk: RiskLevel.Moderate,
        warning:
            "Mobile Hotspot and Internet Connection Sharing stop working, calls through Phone Link stop, " +
            "offline maps no longer update, Microsoft Family parental controls are not enforced on this device, " +
            "and Windows Insider builds cannot be received.",
        includeInDragCar: false,
        services:
        [
            "RetailDemo",
            "WalletService",
            "SEMgrSvc",
            "PhoneSvc",
            "MapsBroker",
            "SharedAccess",
            "icssvc",
            "wisvc",
            "WpcMonSvc",
            "WMPNetworkSvc",
            "AxInstSV"
        ]);

    public static Tweak SysMain => DisableServices(
        id: "services.sysmain",
        name: "Disable SysMain (SuperFetch)",
        description:
            "Stops Windows preloading frequently used apps into RAM. On an NVMe drive the benefit is small, " +
            "and on a 16 GB handheld that memory is better left free for the game and the iGPU.",
        risk: RiskLevel.Safe,
        warning: null,
        includeInDragCar: true,
        services: ["SysMain"]);

    public static Tweak SearchIndexer => DisableServices(
        id: "services.wsearch",
        name: "Disable Search Indexer",
        description:
            "Stops the Windows Search service indexing files in the background, which removes a steady " +
            "source of SSD reads and CPU wake-ups.",
        risk: RiskLevel.Moderate,
        warning:
            "Searching for files and file contents in Start and File Explorer becomes slower, and apps that " +
            "rely on the index (Outlook search, for example) will return fewer results. Launching apps from " +
            "Start by name still works.",
        includeInDragCar: false,
        services: ["WSearch"]);

    public static Tweak PrintAndFax => DisableServices(
        id: "services.printfax",
        name: "Disable Print Spooler & Fax",
        description:
            "Stops the print and fax services. As a side benefit, a disabled spooler closes off the whole " +
            "PrintNightmare family of vulnerabilities.",
        risk: RiskLevel.Moderate,
        warning:
            "Nothing can print while this is on, including \"Microsoft Print to PDF\". Revert it before " +
            "printing or adding a printer.",
        includeInDragCar: false,
        services: ["Spooler", "Fax"]);

    private static Tweak DisableServices(
        string id,
        string name,
        string description,
        RiskLevel risk,
        string? warning,
        bool includeInDragCar,
        string[] services) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Category = TweakCategory.Services,
        Risk = risk,
        Warning = warning,
        IncludeInDragCar = includeInDragCar,

        // Detection reads the registry directly: it is kept in step because changes go through sc.exe,
        // and it avoids starting a PowerShell process for every card on the page.
        ScriptDetect = (ctx, _) =>
        {
            var startTypes = services
                .Select(s => ctx.Registry.ReadValue(RegistryRoot.LocalMachine, $@"{ServicesKey}\{s}", "Start"))
                .OfType<int>()
                .ToList();

            if (startTypes.Count == 0)
            {
                // None of these services exist on this edition of Windows (Fax is absent on Home).
                return Task.FromResult(TweakState.Unknown);
            }

            var disabled = startTypes.Count(t => t == 4);

            return Task.FromResult(
                disabled == startTypes.Count ? TweakState.Applied
                : disabled == 0 ? TweakState.NotApplied
                : TweakState.Partial);
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            foreach (var service in services)
            {
                if (SafetyGuard.IsServiceProtected(service, out var reason))
                {
                    ctx.Log.Error($"BLOCKED service {service}: {reason}");
                    return TweakResult.Blocked(id, reason!);
                }
            }

            // Only services that exist and are not already disabled are touched, and only those are
            // journalled, so revert never "restores" a service to a state we did not find it in.
            var toChange = new List<string>();

            foreach (var service in services)
            {
                var start = ctx.Registry.ReadValue(RegistryRoot.LocalMachine, $@"{ServicesKey}\{service}", "Start");

                if (start is not int startType)
                {
                    ctx.Log.Trace($"    {service}: not installed on this system, skipping");
                    continue;
                }

                if (startType == 4)
                {
                    ctx.Log.Trace($"    {service}: already disabled");
                    continue;
                }

                var delayed = ctx.Registry.ReadValue(RegistryRoot.LocalMachine, $@"{ServicesKey}\{service}", "DelayedAutostart");

                journal.CapturedState[$"service.{service}.start"] = startType.ToString();
                journal.CapturedState[$"service.{service}.delayed"] = delayed is int d && d == 1 ? "1" : "0";
                toChange.Add(service);
            }

            if (toChange.Count == 0)
            {
                return TweakResult.NoChange(id);
            }

            var list = string.Join(",", toChange.Select(s => $"'{s}'"));

            var status = await ctx.Runner.RunScriptAsync(
                $$"""
                foreach ($n in @({{list}})) {
                    $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                    if ($s) { Write-Output "SVC=$n=$($s.Status)" }
                }
                """,
                "Capture service run state before changing it",
                ct,
                echoScript: false);

            foreach (var line in status.OutputLines.Where(l => l.StartsWith("SVC=", StringComparison.Ordinal)))
            {
                var parts = line.Split('=', 3);
                if (parts.Length == 3)
                {
                    journal.CapturedState[$"service.{parts[1]}.status"] = parts[2];
                }
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                $$"""
                $ErrorActionPreference = 'Continue'
                $failed = $false
                foreach ($n in @({{list}})) {
                    Write-Output "Setting $n start type to Disabled"
                    & sc.exe config $n start= disabled | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        Write-Output "sc.exe config failed for $n (exit $LASTEXITCODE)"
                        $failed = $true
                        continue
                    }
                    $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                    if ($s -and $s.Status -ne 'Stopped') {
                        Write-Output "Stopping $n"
                        Stop-Service -Name $n -Force -ErrorAction Continue
                    }
                }
                if ($failed) { exit 1 }
                """,
                $"Disable services: {string.Join(", ", toChange)}",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok(id, $"\"{name}\" applied.")
                : TweakResult.Fail(id, "One or more services could not be disabled. See log.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            var lines = new List<string>();

            foreach (var service in services)
            {
                if (!journal.CapturedState.TryGetValue($"service.{service}.start", out var start))
                {
                    continue;
                }

                if (SafetyGuard.IsServiceProtected(service, out var reason))
                {
                    ctx.Log.Error($"BLOCKED service {service}: {reason}");
                    return TweakResult.Blocked(id, reason!);
                }

                journal.CapturedState.TryGetValue($"service.{service}.delayed", out var delayed);
                journal.CapturedState.TryGetValue($"service.{service}.status", out var runState);

                var scStart = start switch
                {
                    "2" when delayed == "1" => "delayed-auto",
                    "2" => "auto",
                    "3" => "demand",
                    "4" => "disabled",
                    _ => "demand"
                };

                lines.Add($"Restore-Svc '{service}' '{scStart}' {(runState == "Running" ? "$true" : "$false")}");
            }

            if (lines.Count == 0)
            {
                return TweakResult.NoChange(id);
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                $$"""
                $ErrorActionPreference = 'Continue'
                $script:failed = $false
                function Restore-Svc($n, $start, $wasRunning) {
                    Write-Output "Setting $n start type to $start"
                    & sc.exe config $n start= $start | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        Write-Output "sc.exe config failed for $n (exit $LASTEXITCODE)"
                        $script:failed = $true
                        return
                    }
                    if ($wasRunning) {
                        Write-Output "Starting $n"
                        Start-Service -Name $n -ErrorAction Continue
                    }
                }
                {{string.Join("\n", lines)}}
                if ($script:failed) { exit 1 }
                """,
                $"Restore services for \"{name}\"",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok(id, $"\"{name}\" reverted.")
                : TweakResult.Fail(id, "One or more services could not be restored. See log.");
        }
    };
}
