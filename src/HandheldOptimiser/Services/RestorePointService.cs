using System.IO;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public sealed record RestorePointResult(bool Created, string Message)
{
    public static RestorePointResult Ok(string message) => new(true, message);
    public static RestorePointResult Failed(string message) => new(false, message);
}

/// <summary>
/// Creates a System Restore checkpoint before any tweak runs.
///
/// Two things make this harder than calling Checkpoint-Computer. ASUS handhelds frequently ship with
/// System Protection switched off entirely, and Windows silently refuses more than one checkpoint per
/// 24 hours. Both are handled here, and the result is verified by comparing restore point sequence
/// numbers before and after rather than trusting the exit code.
/// </summary>
public sealed class RestorePointService(LogService log, PowerShellRunner runner, RegistryHelper registry)
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValueName = "SystemRestorePointCreationFrequency";

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;
    private readonly RegistryHelper _registry = registry;

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        _log.Info($"Creating System Restore point: \"{description}\"");

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        // Windows throttles checkpoints to one per 24h. Setting the interval to 0 for the duration of
        // this call is the documented way to guarantee we actually get one.
        var frequencySnapshot = _registry.WriteValue(new RegistryValueSpec(
            RegistryRoot.LocalMachine, SystemRestoreKey, FrequencyValueName, 0, RegistryValueKind.DWord));

        if (frequencySnapshot is null)
        {
            _log.Warning("Could not clear the restore point throttle; a checkpoint may be skipped if one was made recently.");
        }

        try
        {
            var before = await GetLatestSequenceNumberAsync(ct);
            _log.Trace($"Latest restore point sequence before: {(before?.ToString() ?? "none")}");

            var driveLetter = systemDrive.TrimEnd('\\');
            var safeDescription = description.Replace("'", "''");

            var script = $$"""
                $ErrorActionPreference = 'Stop'
                try {
                    Enable-ComputerRestore -Drive '{{systemDrive}}'
                    Write-Output 'PROTECTION_ENABLED'
                } catch {
                    Write-Output "PROTECTION_WARNING: $($_.Exception.Message)"
                }

                # Give VSS room for the shadow copy, but only ever grow its cap. Shrinking it makes VSS
                # delete the oldest shadow copies to fit, and those are the user's older restore points.
                # If the current cap cannot be read, it is left alone.
                try {
                    $vol = Get-CimInstance -ClassName Win32_Volume -Filter "DriveLetter = '{{driveLetter}}'"
                    $storage = Get-CimInstance -ClassName Win32_ShadowStorage |
                        Where-Object { $_.Volume.DeviceID -eq $vol.DeviceID } |
                        Select-Object -First 1
                    if ($null -eq $storage) {
                        Write-Output 'Shadow storage cap not found; leaving it as it is.'
                    } elseif ($storage.MaxSpace -lt 10GB) {
                        Write-Output "Raising shadow storage cap from $([math]::Round($storage.MaxSpace / 1GB, 1)) GB to 10 GB"
                        $null = & vssadmin.exe Resize ShadowStorage /For={{driveLetter}} /On={{driveLetter}} /MaxSize=10GB
                    } else {
                        Write-Output "Shadow storage cap is $([math]::Round($storage.MaxSpace / 1GB, 1)) GB; leaving it as it is."
                    }
                } catch {
                    Write-Output "Could not read the shadow storage cap ($($_.Exception.Message)); leaving it as it is."
                }

                try {
                    Checkpoint-Computer -Description '{{safeDescription}}' -RestorePointType 'MODIFY_SETTINGS'
                    Write-Output 'CHECKPOINT_CALL_OK'
                } catch {
                    Write-Output "CHECKPOINT_ERROR: $($_.Exception.Message)"
                }
                """;

            var outcome = await _runner.RunScriptAsync(script, "Create System Restore checkpoint", ct);

            if (outcome.StdOut.Contains("PROTECTION_WARNING", StringComparison.Ordinal))
            {
                _log.Warning("System Protection could not be enabled automatically.");
            }

            var after = await GetLatestSequenceNumberAsync(ct);
            _log.Trace($"Latest restore point sequence after: {(after?.ToString() ?? "none")}");

            // The only trustworthy signal is that a new, higher-numbered restore point now exists.
            if (after is not null && (before is null || after > before))
            {
                _log.Success($"Restore point created (sequence {after}).");
                return RestorePointResult.Ok($"Restore point created (sequence {after}).");
            }

            var reason = outcome.StdOut.Contains("CHECKPOINT_ERROR", StringComparison.Ordinal)
                ? outcome.OutputLines.FirstOrDefault(l => l.StartsWith("CHECKPOINT_ERROR", StringComparison.Ordinal))
                  ?? "Checkpoint-Computer reported an error."
                : "No new restore point appeared after the checkpoint call.";

            _log.Error($"Restore point NOT created. {reason}");
            return RestorePointResult.Failed(reason);
        }
        finally
        {
            if (frequencySnapshot is not null)
            {
                _registry.RestoreSnapshot(frequencySnapshot);
            }
        }
    }

    public async Task<bool> IsProtectionEnabledAsync(CancellationToken ct = default)
    {
        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $d = Get-WmiObject -Namespace 'root\default' -Class 'SystemRestore' -List
            if ($null -eq $d) { Write-Output 'UNAVAILABLE'; exit 0 }
            $points = Get-ComputerRestorePoint
            if ($null -eq $points) { Write-Output 'ENABLED_NO_POINTS' } else { Write-Output 'ENABLED' }
            """,
            "Check System Protection status",
            ct,
            echoScript: false);

        return outcome.StdOut.Contains("ENABLED", StringComparison.Ordinal);
    }

    private async Task<long?> GetLatestSequenceNumberAsync(CancellationToken ct)
    {
        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $p = Get-ComputerRestorePoint | Sort-Object -Property SequenceNumber | Select-Object -Last 1
            if ($null -ne $p) { Write-Output "SEQ=$($p.SequenceNumber)" }
            """,
            "Read latest restore point sequence",
            ct,
            echoScript: false);

        var line = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("SEQ=", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        return long.TryParse(line.AsSpan(4), out var seq) ? seq : null;
    }
}
