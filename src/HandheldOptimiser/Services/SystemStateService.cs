using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public enum HealthStatus
{
    Good,
    Warning,
    Bad,
    Unknown,

    /// <summary>A fact worth showing that is neither good nor bad, such as what a power plan holds.</summary>
    Info
}

/// <summary>One line of a table inside a row: a setting, and its value plugged in and on battery.</summary>
public sealed record HealthTableRow(string Label, string PluggedIn, string OnBattery);

public sealed class HealthCheck
{
    /// <summary>The row's group in square brackets, then its title: "[Sleep] Wake timers".</summary>
    public required string Name { get; init; }

    public required string Detail { get; init; }
    public required HealthStatus Status { get; init; }
    public string? Advice { get; init; }

    /// <summary>
    /// The answer in a few words ("Running", "Off", "0.6% an hour"), for a page that is read at a
    /// glance. Left out where <see cref="Detail"/> is already that short.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>How full something is, from 0 to 1, where the row is about space. Drawn as a bar.</summary>
    public double? Used { get; init; }

    /// <summary>The row's values as a table, where it has one per power source.</summary>
    public IReadOnlyList<HealthTableRow> Table { get; init; } = [];

    /// <summary>The group out of <see cref="Name"/>, without its brackets, or empty where there is none.</summary>
    public string Group => Name.StartsWith('[') && Name.IndexOf(']') is > 0 and var end ? Name[1..end] : string.Empty;

    /// <summary>The title out of <see cref="Name"/>, without its group.</summary>
    public string Title => Group.Length == 0 ? Name : Name[(Group.Length + 2)..].TrimStart();

    /// <summary>The label of a button that opens the Windows page for this row, where there is one.</summary>
    public string? OpenText { get; init; }

    /// <summary>What that button opens: a Settings page, as an ms-settings link.</summary>
    public string? OpenUri { get; init; }
}

/// <summary>
/// Read-only inspection of the machine. Backs the Device &amp; System Health page, which deliberately
/// contains no toggles: the user's constraints forbid touching the handheld vendor's software and the
/// AMD, Intel and Realtek drivers, so the useful thing this app can offer for that hardware is proof that
/// it left everything alone. The same page shows what Windows reports about sleep, the power plans and
/// storage (<see cref="SleepHealth"/>, <see cref="ProcessorPowerReport"/>, <see cref="StorageHealth"/>),
/// which is evidence for the tweaks elsewhere and changes nothing either.
/// </summary>
public sealed class SystemStateService(LogService log, PowerShellRunner runner, RegistryHelper registry)
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;
    private readonly RegistryHelper _registry = registry;

    private string? ReadBios(string valueName) =>
        _registry.ReadValue(RegistryRoot.LocalMachine, BiosKey, valueName)?.ToString();

    public string GetHardwareModel()
    {
        var product = ReadBios("SystemProductName");
        var family = ReadBios("SystemFamily");

        if (string.IsNullOrWhiteSpace(product))
        {
            return "Unknown device";
        }

        // ASUS reports the model code twice ("ROG Ally RC71L_RC71L"), and the family usually repeats
        // the start of the product name ("ROG Ally"), so both are only shown once.
        var name = System.Text.RegularExpressions.Regex.Replace(product.Trim(), @"\b(\w+)_\1\b", "$1");
        return string.IsNullOrWhiteSpace(family) || name.Contains(family.Trim(), StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({family.Trim()})";
    }

    public HandheldDevice DetectDevice() => HandheldDevices.Identify(
        ReadBios("SystemManufacturer"),
        ReadBios("SystemProductName"),
        ReadBios("SystemFamily"),
        ReadBios("BaseBoardProduct"));

    /// <summary>
    /// The vendor whose software is inspected on this device and the app it ships, or null where there is
    /// none to inspect (the Steam Deck has only drivers, and the smaller makers are not covered yet).
    /// </summary>
    public (string Vendor, string App)? GetVendorSoftware() => DetectDevice() switch
    {
        HandheldDevice.RogAlly => ("ASUS", "Armoury Crate SE"),
        HandheldDevice.LegionGo => ("Lenovo", "Legion Space"),
        HandheldDevice.MsiClaw => ("MSI", "MSI Center M"),
        _ => null
    };

    private static readonly (string ServiceName, string Friendly, string Group)[] AsusServices =
    [
        ("ArmouryCrateControlInterface", "Armoury Crate Control Interface", "ASUS"),
        ("ARMOURY CRATE Service",        "Armoury Crate Service",           "ASUS"),
        ("ASUSOptimization",             "ASUS Optimization",               "ASUS"),
        ("ASUSSoftwareManager",          "ASUS Software Manager",           "ASUS"),
        ("ASUSSwitch",                   "ASUS Switch",                     "ASUS"),
        ("ASUSSystemAnalysis",           "ASUS System Analysis",            "ASUS"),
        ("AsusAppService",               "ASUS App Service",                "ASUS")
    ];

    // DAService is the service that starts Legion Space's background daemon and keeps it running.
    private static readonly (string ServiceName, string Friendly, string Group)[] LenovoServices =
    [
        ("DAService", "Legion Space Service (DAService)", "Lenovo")
    ];

    private static readonly (string ServiceName, string Friendly, string Group)[] MsiServices =
    [
        ("MSI Foundation Service", "MSI Foundation Service", "MSI"),
        ("MSI_Center_Service",     "MSI Center Service",     "MSI")
    ];

    /// <summary>
    /// Services that must be running or at least not disabled on any handheld: the drivers the user named
    /// as untouchable and the security stack this app refuses to weaken.
    /// </summary>
    private static readonly (string ServiceName, string Friendly, string Group)[] CommonServices =
    [
        ("AMD External Events Utility",  "AMD External Events",             "AMD / Realtek"),
        ("AMDRyzenMasterDriverV24",      "AMD Ryzen Master Driver",         "AMD / Realtek"),
        ("RtkAudUService",               "Realtek Audio Universal Service", "AMD / Realtek"),
        ("Audiosrv",                     "Windows Audio",                   "AMD / Realtek"),
        ("WinDefend",                    "Microsoft Defender Antivirus",    "Security"),
        ("SecurityHealthService",        "Windows Security Health",         "Security"),
        ("mpssvc",                       "Windows Firewall",                "Security"),
        ("wuauserv",                     "Windows Update",                  "Windows Update"),
        ("UsoSvc",                       "Update Orchestrator",             "Windows Update"),
        ("BITS",                         "Background Transfer (BITS)",      "Windows Update"),
        ("GamingServices",               "Gaming Services (Game Pass)",     "Xbox / Game Pass"),
        ("XblAuthManager",               "Xbox Live Auth Manager",          "Xbox / Game Pass")
    ];

    /// <summary>The detected vendor's services first, then the ones every handheld shares.</summary>
    private (string ServiceName, string Friendly, string Group)[] WatchedServices() =>
    [
        .. DetectDevice() switch
        {
            HandheldDevice.RogAlly => AsusServices,
            HandheldDevice.LegionGo => LenovoServices,
            HandheldDevice.MsiClaw => MsiServices,
            _ => []
        },
        .. CommonServices
    ];

    public async Task<IReadOnlyList<HealthCheck>> RunHealthChecksAsync(CancellationToken ct = default)
    {
        _log.Info("Running protected-component health check…");

        var checks = new List<HealthCheck>();

        // Started now and collected at the end, so they run alongside the service checks: between them
        // they wait on powercfg for a couple of seconds.
        var sleep = SleepHealth.RunAsync(_runner, ct);
        var plans = ProcessorPowerReport.RunAsync(_runner, ct);
        var storage = StorageHealth.RunAsync(_runner, ct);

        var watched = WatchedServices();
        var serviceNames = string.Join(",", watched.Select(s => PsQuote.Literal(s.ServiceName)));

        var outcome = await _runner.RunScriptAsync(
            $$"""
             $ErrorActionPreference = 'SilentlyContinue'
             foreach ($n in @({{serviceNames}})) {
                 $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                 if ($null -eq $s) {
                     Write-Output "SVC|$n|ABSENT|ABSENT"
                 } else {
                     Write-Output "SVC|$n|$($s.Status)|$($s.StartType)"
                 }
             }
             """,
            "Query protected service states",
            ct,
            echoScript: false);

        var serviceStates = new Dictionary<string, (string Status, string StartType)>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in outcome.OutputLines.Where(l => l.StartsWith("SVC|", StringComparison.Ordinal)))
        {
            var parts = line.Split('|');
            if (parts.Length >= 4)
            {
                serviceStates[parts[1]] = (parts[2], parts[3]);
            }
        }

        foreach (var (serviceName, friendly, group) in watched)
        {
            if (!serviceStates.TryGetValue(serviceName, out var state) || state.Status == "ABSENT")
            {
                // Not every vendor or driver service exists on every model and firmware revision, so
                // absence is only noteworthy for the security and update stack.
                var absentStatus = group is "Security" or "Windows Update" ? HealthStatus.Bad : HealthStatus.Unknown;

                checks.Add(new HealthCheck
                {
                    Name = $"[{group}] {friendly}",
                    Detail = "Not installed on this system",
                    Summary = "Not installed",
                    Status = absentStatus,
                    Advice = absentStatus == HealthStatus.Bad
                        ? "This service should exist on Windows 11. Investigate before applying tweaks."
                        : null
                });
                continue;
            }

            var disabled = state.StartType.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
            var status = ServiceStatus(state.Status, state.StartType);
            var running = state.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
            var resting = status == HealthStatus.Good && !running;

            checks.Add(new HealthCheck
            {
                Name = $"[{group}] {friendly}",
                Detail = resting ? $"Stopped until Windows needs it, start type {state.StartType}"
                    : status == HealthStatus.Warning ? $"{state.Status}, though it is set to start by itself"
                    : $"{state.Status}, start type {state.StartType}",
                Summary = disabled ? "Disabled" : running ? "Running" : resting ? "Resting" : state.Status,
                Status = status,
                Advice = disabled
                    ? "This service is disabled. Handheld Optimiser never disables it, so another tool likely did. " +
                      "Repair disabled services, at the top of this page, switches it back on."
                    : null
            });
        }

        checks.AddRange(await GetVirtualisationChecksAsync(ct));

        var reported = checks.Count;
        checks.AddRange(await sleep);
        checks.AddRange(await plans);
        checks.AddRange(await storage);

        // Their scripts are not echoed into the log, so what they found is written here.
        foreach (var check in checks.Skip(reported))
        {
            _log.Info($"{check.Name}: {check.Detail.Replace("\n", " | ")}");
        }

        _log.Info($"Health check complete: {checks.Count(c => c.Status == HealthStatus.Good)} good, " +
                  $"{checks.Count(c => c.Status == HealthStatus.Warning)} warnings, " +
                  $"{checks.Count(c => c.Status == HealthStatus.Bad)} problems.");

        return checks;
    }

    /// <summary>
    /// How a watched service stands, from its run state and start type as Get-Service names them.
    /// Disabled is the fault this page exists to find. A service set to Manual starts when something
    /// asks for it and stops itself when the work is done (BITS between downloads, Windows Update
    /// between checks), so stopped is its normal state and not something to look into. Only a service
    /// that should start by itself and is not running is worth a look.
    /// </summary>
    public static HealthStatus ServiceStatus(string status, string startType)
    {
        if (startType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatus.Bad;
        }

        return status.Equals("Running", StringComparison.OrdinalIgnoreCase)
               || startType.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? HealthStatus.Good
            : HealthStatus.Warning;
    }

    private async Task<IReadOnlyList<HealthCheck>> GetVirtualisationChecksAsync(CancellationToken ct)
    {
        var checks = new List<HealthCheck>();

        var hvci = _registry.ReadValue(
            RegistryRoot.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
            "Enabled");

        var hvciOn = hvci is not int i || i != 0;

        checks.Add(new HealthCheck
        {
            Name = "[Performance] Memory Integrity (HVCI)",
            Detail = hvciOn ? "Enabled (costing frame rate)" : "Disabled (optimised for gaming)",
            Summary = hvciOn ? "On" : "Off",
            Status = hvciOn ? HealthStatus.Warning : HealthStatus.Good,
            Advice = hvciOn ? "Disable this on the Performance page for the largest single FPS gain." : null
        });

        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $vbs = (Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard).VirtualizationBasedSecurityStatus
            Write-Output "VBS=$vbs"
            $vmp = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State
            Write-Output "VMP=$vmp"
            """,
            "Query virtualisation state",
            ct,
            echoScript: false);

        var vbs = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("VBS=", StringComparison.Ordinal))?[4..];
        var vmp = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("VMP=", StringComparison.Ordinal))?[4..] ?? "unknown";

        checks.Add(VbsCheck(vbs));

        checks.Add(new HealthCheck
        {
            Name = "[Performance] Virtual Machine Platform",
            Detail = vmp,
            Status = vmp.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? HealthStatus.Good : HealthStatus.Warning
        });

        return checks;
    }

    /// <summary>
    /// Whether VBS is running, from the status Windows itself reports for this boot: 0 is off, 1 is
    /// switched on but not running, 2 is running. This replaced a check of the boot hypervisor setting,
    /// which read "off" on a ROG Ally while VBS ran, and "absent" once VBS had really stopped.
    /// </summary>
    public static HealthCheck VbsCheck(string? status)
    {
        const string name = "[Performance] Virtualisation-based security (VBS)";
        const string advice =
            "VBS adds overhead to every process. Disable Virtual Machine Platform & VBS on the Performance " +
            "page turns it off, then restart.";

        return status?.Trim() switch
        {
            "0" => new HealthCheck { Name = name, Detail = "Off", Status = HealthStatus.Good },
            "1" => new HealthCheck { Name = name, Detail = "Switched on, not running", Status = HealthStatus.Warning, Advice = advice },
            "2" => new HealthCheck { Name = name, Detail = "Running", Status = HealthStatus.Warning, Advice = advice },
            _ => new HealthCheck { Name = name, Detail = "Windows did not say", Status = HealthStatus.Unknown }
        };
    }

    /// <summary>
    /// Re-enables a protected service that some other tool disabled. This app only ever moves these in the
    /// safe direction. There is no code path here that disables them.
    /// </summary>
    public async Task<bool> RepairProtectedServicesAsync(CancellationToken ct = default)
    {
        _log.Info("Repairing any disabled vendor / driver / security / update services…");

        var names = string.Join(",", WatchedServices().Select(s => PsQuote.Literal(s.ServiceName)));

        var outcome = await _runner.RunScriptAsync(
            $$"""
             $ErrorActionPreference = 'Continue'
             foreach ($n in @({{names}})) {
                 $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                 if ($null -eq $s) { continue }
                 if ($s.StartType -eq 'Disabled') {
                     Write-Output "Re-enabling disabled service: $n"
                     Set-Service -Name $n -StartupType Automatic -ErrorAction Continue
                     Start-Service -Name $n -ErrorAction Continue
                 }
             }
             Write-Output 'REPAIR_DONE'
             """,
            "Repair disabled protected services",
            ct);

        return outcome.StdOut.Contains("REPAIR_DONE", StringComparison.Ordinal);
    }
}
