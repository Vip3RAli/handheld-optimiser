using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public enum HealthStatus
{
    Good,
    Warning,
    Bad,
    Unknown
}

public sealed class HealthCheck
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required HealthStatus Status { get; init; }
    public string? Advice { get; init; }
}

/// <summary>
/// Read-only inspection of the machine. Backs the ASUS page, which deliberately contains no toggles: the
/// user's constraints forbid touching ASUS, AMD and Realtek software, so the useful thing this app can
/// offer for that hardware is proof that it left everything alone.
/// </summary>
public sealed class SystemStateService(LogService log, PowerShellRunner runner, RegistryHelper registry)
{
    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;
    private readonly RegistryHelper _registry = registry;

    public string GetHardwareModel()
    {
        var product = _registry.ReadValue(
            RegistryRoot.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\BIOS",
            "SystemProductName")?.ToString();

        var family = _registry.ReadValue(
            RegistryRoot.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\BIOS",
            "SystemFamily")?.ToString();

        return string.IsNullOrWhiteSpace(product) ? "Unknown device" : $"{product} ({family})".Trim();
    }

    public bool IsRogAlly()
    {
        var model = GetHardwareModel();
        return model.Contains("ROG Ally", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("RC71", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("RC72", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Services that must be running or at least not disabled. Split into the ones the user named as
    /// untouchable and the security stack this app refuses to weaken.
    /// </summary>
    private static readonly (string ServiceName, string Friendly, string Group)[] WatchedServices =
    [
        ("ArmouryCrateControlInterface", "Armoury Crate Control Interface", "ASUS"),
        ("ARMOURY CRATE Service",        "Armoury Crate Service",           "ASUS"),
        ("ASUSOptimization",             "ASUS Optimization",               "ASUS"),
        ("ASUSSoftwareManager",          "ASUS Software Manager",           "ASUS"),
        ("ASUSSwitch",                   "ASUS Switch",                     "ASUS"),
        ("ASUSSystemAnalysis",           "ASUS System Analysis",            "ASUS"),
        ("AsusAppService",               "ASUS App Service",                "ASUS"),
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

    public async Task<IReadOnlyList<HealthCheck>> RunHealthChecksAsync(CancellationToken ct = default)
    {
        _log.Info("Running protected-component health check…");

        var checks = new List<HealthCheck>();

        var serviceNames = string.Join(",", WatchedServices.Select(s => $"'{s.ServiceName.Replace("'", "''")}'"));

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

        foreach (var (serviceName, friendly, group) in WatchedServices)
        {
            if (!serviceStates.TryGetValue(serviceName, out var state) || state.Status == "ABSENT")
            {
                // Not every ASUS service exists on every firmware revision, so absence is only
                // noteworthy for the security and update stack.
                var absentStatus = group is "Security" or "Windows Update" ? HealthStatus.Bad : HealthStatus.Unknown;

                checks.Add(new HealthCheck
                {
                    Name = $"[{group}] {friendly}",
                    Detail = "Not installed on this system",
                    Status = absentStatus,
                    Advice = absentStatus == HealthStatus.Bad
                        ? "This service should exist on Windows 11. Investigate before applying tweaks."
                        : null
                });
                continue;
            }

            var disabled = state.StartType.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
            var running = state.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);

            var status = disabled ? HealthStatus.Bad
                : running ? HealthStatus.Good
                : HealthStatus.Warning;

            checks.Add(new HealthCheck
            {
                Name = $"[{group}] {friendly}",
                Detail = $"{state.Status}, start type {state.StartType}",
                Status = status,
                Advice = disabled
                    ? "This service is disabled. Handheld Optimiser never disables it — another tool likely did. " +
                      "Re-enable it from the repair action below."
                    : null
            });
        }

        checks.AddRange(await GetVirtualisationChecksAsync(ct));

        _log.Info($"Health check complete: {checks.Count(c => c.Status == HealthStatus.Good)} good, " +
                  $"{checks.Count(c => c.Status == HealthStatus.Warning)} warnings, " +
                  $"{checks.Count(c => c.Status == HealthStatus.Bad)} problems.");

        return checks;
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
            Detail = hvciOn ? "Enabled — costing frame rate" : "Disabled — optimised for gaming",
            Status = hvciOn ? HealthStatus.Warning : HealthStatus.Good,
            Advice = hvciOn ? "Disable this on the Gaming Tweaks page for the largest single FPS gain." : null
        });

        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $line = bcdedit /enum '{current}' | Select-String -Pattern 'hypervisorlaunchtype'
            if ($line) { Write-Output "BCD=$(($line.ToString().Trim() -split '\s+')[-1])" } else { Write-Output 'BCD=absent' }
            $vmp = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State
            Write-Output "VMP=$vmp"
            """,
            "Query virtualisation state",
            ct,
            echoScript: false);

        var bcd = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("BCD=", StringComparison.Ordinal))?[4..] ?? "unknown";
        var vmp = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("VMP=", StringComparison.Ordinal))?[4..] ?? "unknown";

        var hypervisorOff = bcd.Equals("off", StringComparison.OrdinalIgnoreCase);

        checks.Add(new HealthCheck
        {
            Name = "[Performance] Boot hypervisor",
            Detail = $"hypervisorlaunchtype = {bcd}",
            Status = hypervisorOff ? HealthStatus.Good : HealthStatus.Warning,
            Advice = hypervisorOff ? null : "Disabling the boot hypervisor removes VBS overhead from every process."
        });

        checks.Add(new HealthCheck
        {
            Name = "[Performance] Virtual Machine Platform",
            Detail = vmp,
            Status = vmp.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? HealthStatus.Good : HealthStatus.Warning
        });

        return checks;
    }

    /// <summary>
    /// Re-enables a protected service that some other tool disabled. This app only ever moves these in the
    /// safe direction — there is no code path here that disables them.
    /// </summary>
    public async Task<bool> RepairProtectedServicesAsync(CancellationToken ct = default)
    {
        _log.Info("Repairing any disabled ASUS / AMD / security / update services…");

        var names = string.Join(",", WatchedServices.Select(s => $"'{s.ServiceName.Replace("'", "''")}'"));

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
