using HandheldOptimiser.Models;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.Services;

/// <summary>
/// Scans for and removes packaged apps from the curated catalog.
///
/// Removal is the one operation in this app that the undo journal cannot reverse — getting an app back
/// means reinstalling it from the Store. The journal still records what went, so there is a definitive
/// list to reinstall from, and the UI says so plainly rather than implying a revert button will fix it.
/// </summary>
public sealed class AppxService(LogService log, PowerShellRunner runner)
{
    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    /// <summary>
    /// Returns catalog entries that are actually present on this machine, so the UI never offers to
    /// remove something that is already gone.
    /// </summary>
    public async Task<IReadOnlyList<AppxPresence>> ScanAsync(CancellationToken ct = default)
    {
        _log.Info("Scanning for installed packaged apps…");

        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            foreach ($p in Get-AppxPackage) {
                Write-Output "USER|$($p.Name)|$($p.PackageFullName)"
            }
            foreach ($p in Get-AppxProvisionedPackage -Online) {
                Write-Output "PROV|$($p.DisplayName)|$($p.PackageName)"
            }
            """,
            "Enumerate installed and provisioned packages",
            ct,
            echoScript: false);

        var userPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var provisioned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in outcome.OutputLines)
        {
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[1].Trim();
            var full = parts[2].Trim();

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (parts[0] == "USER")
            {
                userPackages[name] = full;
            }
            else if (parts[0] == "PROV")
            {
                provisioned[name] = full;
            }
        }

        var results = new List<AppxPresence>();

        foreach (var target in AppxCatalog.All)
        {
            var presence = new AppxPresence { Target = target };

            if (userPackages.TryGetValue(target.IdentityName, out var fullName))
            {
                presence.InstalledForUser = true;
                presence.FullPackageName = fullName;
            }

            if (provisioned.ContainsKey(target.IdentityName))
            {
                presence.Provisioned = true;
            }

            if (presence.IsPresent)
            {
                results.Add(presence);
            }
        }

        _log.Info($"Found {results.Count} removable app(s) from a catalog of {AppxCatalog.All.Count}.");
        return results;
    }

    /// <summary>
    /// Removes the given packages for the current user, for all users, and from the provisioned image so
    /// Windows does not reinstate them on the next major update.
    /// </summary>
    public async Task<(int Removed, int Failed)> RemoveAsync(
        IReadOnlyList<AppxTarget> targets,
        TweakJournalEntry journal,
        CancellationToken ct = default)
    {
        var allowed = new List<AppxTarget>();

        foreach (var target in targets)
        {
            if (SafetyGuard.IsAppxProtected(target.IdentityName, out var reason))
            {
                _log.Error($"BLOCKED removal of {target.IdentityName} — {reason}");
                continue;
            }

            allowed.Add(target);
        }

        if (allowed.Count == 0)
        {
            _log.Warning("No packages left to remove after safety filtering.");
            return (0, 0);
        }

        var removed = 0;
        var failed = 0;

        foreach (var target in allowed)
        {
            ct.ThrowIfCancellationRequested();

            _log.Info($"Removing {target.FriendlyName} ({target.IdentityName})");

            var safeName = target.IdentityName.Replace("'", "''");

            var script = $$"""
                $ErrorActionPreference = 'Continue'
                $name = '{{safeName}}'
                $ok = $true

                $pkgs = Get-AppxPackage -Name $name -ErrorAction SilentlyContinue
                foreach ($p in $pkgs) {
                    Write-Output "Remove-AppxPackage $($p.PackageFullName)"
                    try { Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop }
                    catch { Write-Output "  user-scope removal failed: $($_.Exception.Message)"; $ok = $false }
                }

                $allUsers = Get-AppxPackage -AllUsers -Name $name -ErrorAction SilentlyContinue
                foreach ($p in $allUsers) {
                    Write-Output "Remove-AppxPackage -AllUsers $($p.PackageFullName)"
                    try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop }
                    catch { Write-Output "  all-users removal skipped: $($_.Exception.Message)" }
                }

                $prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name }
                foreach ($p in $prov) {
                    Write-Output "Remove-AppxProvisionedPackage $($p.PackageName)"
                    try { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -ErrorAction Stop | Out-Null }
                    catch { Write-Output "  provisioned removal failed: $($_.Exception.Message)"; $ok = $false }
                }

                if ($ok) { Write-Output 'RESULT=OK' } else { Write-Output 'RESULT=PARTIAL' }
                """;

            var outcome = await _runner.RunScriptAsync(script, $"Remove {target.FriendlyName}", ct, echoScript: false);

            if (outcome.StdOut.Contains("RESULT=OK", StringComparison.Ordinal))
            {
                _log.Success($"Removed {target.FriendlyName}");
                journal.CapturedState[$"appx.{target.IdentityName}"] = target.FriendlyName;
                removed++;
            }
            else
            {
                _log.Warning($"{target.FriendlyName} was not fully removed.");
                failed++;
            }
        }

        _log.Info($"Bloatware removal finished: {removed} removed, {failed} with problems.");
        return (removed, failed);
    }
}
