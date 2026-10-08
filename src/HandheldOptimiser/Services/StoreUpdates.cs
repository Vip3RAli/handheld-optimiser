using System.Text.RegularExpressions;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Microsoft Store app updates, through the Store's own install queue (the AppInstallManager WinRT
/// class, called from PowerShell). The Store downloads and installs; this class asks it which apps
/// have updates, asks it to update the chosen ones, and follows each one until the Store says it is done.
/// </summary>
public sealed class StoreUpdates(LogService log, PowerShellRunner runner)
{
    /// <summary>Lines starting with this are data for this class rather than messages for the console.</summary>
    private const string Tag = "@@";

    /// <summary>A package family name: the package name, an underscore and the 13 character publisher hash.</summary>
    private static readonly Regex FamilyNameShape = new(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads the WinRT types and a helper that waits on their async calls, which Windows PowerShell has no
    /// built-in way to await.
    /// </summary>
    private const string Prelude = """
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        function Await($operation, [Type]$type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
            if (-not $task.Wait(300000)) { throw 'The Microsoft Store did not answer in time.' }
            $task.Result
        }
        $null = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager, Windows.ApplicationModel.Store.Preview, ContentType = WindowsRuntime]
        $null = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem, Windows.ApplicationModel.Store.Preview, ContentType = WindowsRuntime]
        $null = [Windows.ApplicationModel.Store.Preview.InstallControl.AppUpdateOptions, Windows.ApplicationModel.Store.Preview, ContentType = WindowsRuntime]
        $null = [Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType = WindowsRuntime]
        $manager = New-Object Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager
        """;

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    public async Task<UpdateScan> ScanAsync(CancellationToken ct)
    {
        // Searching without installing still puts each update in the Store's queue, paused. Those are
        // cancelled once listed so nothing is left waiting there; installing asks the Store again. Only
        // paused ones are touched, so an update the Store was already installing carries on.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
            {{Prelude}}
                $options = New-Object Windows.ApplicationModel.Store.Preview.InstallControl.AppUpdateOptions
                $options.AutomaticallyDownloadAndInstallUpdateIfFound = $false
                $listType = [System.Collections.Generic.IReadOnlyList[Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem]]
                $found = Await ($manager.SearchForAllUpdatesAsync('', '', $options)) $listType
                $packages = New-Object Windows.Management.Deployment.PackageManager

                foreach ($item in $found) {
                    $pfn = $item.PackageFamilyName
                    $name = ($pfn -split '_')[0]
                    $version = ''
                    try {
                        $package = $packages.FindPackagesForUser('', $pfn) | Select-Object -First 1
                        if ($package) {
                            if ($package.DisplayName) { $name = $package.DisplayName }
                            $v = $package.Id.Version
                            $version = "$($v.Major).$($v.Minor).$($v.Build).$($v.Revision)"
                        }
                    } catch { }

                    try {
                        if ("$($item.GetCurrentStatus().InstallState)" -eq 'Paused') { $item.Cancel() }
                    } catch { }

                    "{{Tag}}`t$pfn`t$($item.ProductId)`t$version`t$($name -replace '\s+', ' ')"
                }
                exit 0
            } catch {
                "{{Tag}}ERROR`t$('0x{0:X8}' -f $_.Exception.HResult)`t$($_.Exception.Message -replace '\s+', ' ')"
                exit 1
            }
            """;

        var outcome = await _runner.RunScriptAsync(script, "Check Microsoft Store apps for updates", ct, echoScript: false, logOutput: false);

        var error = outcome.OutputLines.FirstOrDefault(l => l.StartsWith(Tag + "ERROR", StringComparison.Ordinal));
        if (!outcome.Succeeded || error is not null)
        {
            var parts = error?.Split('\t') ?? [];
            _log.Warning($"    Store check failed: {(parts.Length > 2 ? $"{parts[1]} {parts[2]}" : outcome.StdErr.Trim())}");
            return UpdateScan.Failed("The Microsoft Store could not be asked for updates from here. Open the Store to update apps there.");
        }

        var items = new List<UpdateItem>();

        foreach (var line in outcome.OutputLines.Where(l => l.StartsWith(Tag + "\t", StringComparison.Ordinal)))
        {
            var parts = line.Split('\t');
            if (parts.Length < 5 || !FamilyNameShape.IsMatch(parts[1]))
            {
                continue;
            }

            // The Store does not say which version it will install, only that there is a newer one.
            items.Add(new UpdateItem(
                UpdateSource.Store, parts[1], parts[4], parts[3].Length > 0 ? parts[3] : null, null, parts[2]));
            _log.Trace($"    {parts[4]} ({parts[1]})");
        }

        _log.Info($"Microsoft Store: {items.Count} app update(s) available.");
        return new UpdateScan(items);
    }

    /// <summary>
    /// Asks the Store to update each app in turn and waits for it to finish. An update the Store pauses
    /// (low battery, or a large download on a metered connection) is left to the Store after a minute
    /// rather than holding up the rest.
    /// </summary>
    public async Task<IReadOnlyList<(UpdateItem Item, UpdateOutcome Outcome)>> InstallAsync(
        IReadOnlyList<UpdateItem> items,
        IProgress<UpdateProgress> progress,
        CancellationToken ct)
    {
        var results = new Dictionary<string, (UpdateItem Item, UpdateOutcome Outcome)>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, UpdateItem>(StringComparer.OrdinalIgnoreCase);

        // Only names of the expected shape reach the script, and they are quoted there as literals.
        foreach (var item in items)
        {
            if (FamilyNameShape.IsMatch(item.Key))
            {
                byName[item.Key] = item;
            }
            else
            {
                results[item.Key] = (item, UpdateOutcome.Fail("Its package name could not be read, so it was not updated."));
            }
        }

        if (byName.Count == 0)
        {
            return results.Values.ToList();
        }

        var wanted = string.Join(", ", byName.Keys.Select(k => $"'{k}'"));

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $wanted = @({{wanted}})
            try {
            {{Prelude}}
            } catch {
                foreach ($pfn in $wanted) { "{{Tag}}DONE`t$pfn`tfailed`t$('0x{0:X8}' -f $_.Exception.HResult)" }
                exit 1
            }

            $itemType = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem]
            foreach ($pfn in $wanted) {
                try {
                    "{{Tag}}STEP`t$pfn`tPending`t0"
                    $item = Await ($manager.UpdateAppByPackageFamilyNameAsync($pfn)) $itemType
                    if ($null -eq $item) { "{{Tag}}DONE`t$pfn`tcurrent`t"; continue }

                    Write-Output "The Store is updating $pfn"
                    $last = ''; $lastPercent = -10; $pausedSince = $null
                    $deadline = (Get-Date).AddMinutes(30)
                    while ($true) {
                        try { $status = $item.GetCurrentStatus() } catch {
                            # The Store drops an item from its queue once it is done with it.
                            if ($last -eq 'Installing' -or $last -eq 'Completed') { "{{Tag}}DONE`t$pfn`tok`t" }
                            else { "{{Tag}}DONE`t$pfn`tfailed`tlost" }
                            break
                        }

                        $state = "$($status.InstallState)"
                        $percent = [int]$status.PercentComplete
                        if ($state -ne $last -or $percent -ge $lastPercent + 5) {
                            "{{Tag}}STEP`t$pfn`t$state`t$percent"
                            $last = $state; $lastPercent = $percent
                        }

                        if ($state -eq 'Completed') { "{{Tag}}DONE`t$pfn`tok`t"; break }
                        if ($state -eq 'Canceled') { "{{Tag}}DONE`t$pfn`tfailed`tCanceled"; break }
                        if ($state -eq 'Error') {
                            $code = 'unknown'
                            try { $code = '0x{0:X8}' -f $status.ErrorCode.HResult } catch { }
                            "{{Tag}}DONE`t$pfn`tfailed`t$code"
                            break
                        }

                        if ($state -like 'Paused*') {
                            if ($null -eq $pausedSince) { $pausedSince = Get-Date }
                            elseif (((Get-Date) - $pausedSince).TotalSeconds -gt 60) { "{{Tag}}DONE`t$pfn`tfailed`t$state"; break }
                        } else {
                            $pausedSince = $null
                        }

                        if ((Get-Date) -gt $deadline) { "{{Tag}}DONE`t$pfn`tfailed`tTimeout"; break }
                        Start-Sleep -Milliseconds 750
                    }
                } catch {
                    "{{Tag}}DONE`t$pfn`tfailed`t$('0x{0:X8}' -f $_.Exception.HResult)"
                }
            }
            exit 0
            """;

        void OnLine(string line)
        {
            if (!line.StartsWith(Tag, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _log.Trace($"    {line}");
                }

                return;
            }

            var parts = line.Split('\t');
            if (parts.Length < 4 || !byName.TryGetValue(parts[1], out var item))
            {
                return;
            }

            if (parts[0] == Tag + "STEP" && parts.Length >= 4)
            {
                progress.Report(new UpdateProgress(item, Describe(parts[2], parts[3])));
            }
            else if (parts[0] == Tag + "DONE")
            {
                var outcome = parts[2] switch
                {
                    "ok" => UpdateOutcome.Ok("Updated."),
                    "current" => UpdateOutcome.Current("Already up to date."),
                    _ => UpdateOutcome.Fail(Explain(parts[3]))
                };

                lock (results)
                {
                    results[item.Key] = (item, outcome);
                }

                progress.Report(new UpdateProgress(item, outcome.Message, outcome));
            }
        }

        _log.Info($"=== Updating {byName.Count} Microsoft Store app(s) ===");
        var run = await _runner.RunScriptAsync(
            script, "Update Microsoft Store apps", ct, echoScript: false, logOutput: false, onOutputLine: OnLine);

        lock (results)
        {
            foreach (var item in byName.Values.Where(i => !results.ContainsKey(i.Key)))
            {
                var outcome = UpdateOutcome.Fail($"The Store stopped before reaching it (exit {run.ExitCode}).");
                results[item.Key] = (item, outcome);
                progress.Report(new UpdateProgress(item, outcome.Message, outcome));
            }

            foreach (var (item, outcome) in results.Values)
            {
                if (outcome.Kind == UpdateOutcomeKind.Failed)
                {
                    _log.Error($"{item.Name}: {outcome.Message}");
                }
                else
                {
                    _log.Success($"{item.Name}: {outcome.Message}");
                }
            }

            return results.Values.ToList();
        }
    }

    private static string Describe(string state, string percent) => state switch
    {
        "Downloading" => $"Downloading… {percent}%",
        "Installing" => "Installing…",
        "AcquiringLicense" => "Getting the licence…",
        "RestoringData" => "Restoring data…",
        "Completed" => "Finishing…",
        _ when state.StartsWith("Paused", StringComparison.Ordinal) => "Paused by the Store…",
        _ => "Waiting for the Store…"
    };

    private static string Explain(string code) => code switch
    {
        "Canceled" => "The update was cancelled in the Store.",
        "PausedLowBattery" => "The Store paused it because the battery is low. Plug in and try again.",
        "PausedWiFiRecommended" or "PausedWiFiRequired" => "The Store paused it to wait for Wi-Fi. It will carry on there.",
        _ when code.StartsWith("Paused", StringComparison.Ordinal) => "The Store paused it. It will carry on in the Store.",
        "Timeout" => "Still going after 30 minutes. The Store will carry on with it.",
        "lost" => "The Store stopped reporting on it. Open the Store to check.",
        _ => $"The Store could not update it ({code})."
    };
}
