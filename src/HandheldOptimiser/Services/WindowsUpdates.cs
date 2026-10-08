using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Windows Update, through the Windows Update Agent that Settings uses (the Microsoft.Update.Session COM
/// object, run from PowerShell so the console shows exactly what ran). It searches the same catalogue
/// as Settings and installs one update at a time so each row can show how it went.
///
/// Optional ("preview") updates are left out, as Settings leaves them out until asked. Drivers are only
/// listed when the page asks for them: Windows Update sometimes offers a generic driver in place of the
/// one ASUS ships for the Ally.
/// </summary>
public sealed class WindowsUpdates(LogService log, PowerShellRunner runner)
{
    private const int UpdateTypeDriver = 2;

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    /// <summary>Lines starting with this are data for this class rather than messages for the console.</summary>
    private const string Tag = "@@";

    public async Task<UpdateScan> ScanAsync(bool includeDrivers, CancellationToken ct)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $session.ClientApplicationID = 'Handheld Optimiser'
                $found = $session.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0')
                foreach ($u in $found.Updates) {
                    if ($u.BrowseOnly) { continue }
                    if ($u.Type -eq {{UpdateTypeDriver}} -and -not ${{(includeDrivers ? "true" : "false")}}) { continue }
                    $kb = ($u.KBArticleIDs | ForEach-Object { "KB$_" }) -join ', '
                    $size = [math]::Round($u.MaxDownloadSize / 1MB)
                    $title = $u.Title -replace '\s+', ' '
                    "{{Tag}}`t$($u.Identity.UpdateID)`t$kb`t$size`t$($u.Type)`t$title"
                }
                exit 0
            } catch {
                "{{Tag}}ERROR`t$('0x{0:X8}' -f $_.Exception.HResult)`t$($_.Exception.Message -replace '\s+', ' ')"
                exit 1
            }
            """;

        var outcome = await _runner.RunScriptAsync(script, "Check Windows Update", ct, echoScript: false, logOutput: false);

        var error = outcome.OutputLines.FirstOrDefault(l => l.StartsWith(Tag + "ERROR", StringComparison.Ordinal));
        if (!outcome.Succeeded || error is not null)
        {
            var parts = error?.Split('\t') ?? [];
            var code = parts.Length > 1 ? parts[1] : $"exit {outcome.ExitCode}";
            _log.Warning($"    Windows Update search failed: {code} {(parts.Length > 2 ? parts[2] : outcome.StdErr.Trim())}");
            return UpdateScan.Failed(Explain(code) ?? $"Windows Update could not be checked ({code}).");
        }

        var items = new List<UpdateItem>();

        foreach (var line in outcome.OutputLines.Where(l => l.StartsWith(Tag + "\t", StringComparison.Ordinal)))
        {
            var parts = line.Split('\t');
            if (parts.Length < 6 || !Guid.TryParse(parts[1], out var id))
            {
                continue;
            }

            var kb = parts[2];
            var sizeMb = parts[3];
            var isDriver = parts[4] == UpdateTypeDriver.ToString();
            var detail = string.Join(" · ", new[]
            {
                isDriver ? "Driver" : null,
                kb.Length > 0 ? kb : null,
                sizeMb is "0" or "" ? null : $"{sizeMb} MB"
            }.OfType<string>());

            items.Add(new UpdateItem(UpdateSource.WindowsUpdate, id.ToString(), parts[5], null, null, detail));
            _log.Trace($"    {parts[5]}");
        }

        _log.Info($"Windows Update: {items.Count} update(s) available.");
        return new UpdateScan(items);
    }

    /// <summary>
    /// Downloads and installs the chosen updates. The search is run again first because the agent only
    /// installs update objects from its own search, and anything that has since installed in the
    /// background is reported as already done rather than as an error.
    /// </summary>
    public async Task<IReadOnlyList<(UpdateItem Item, UpdateOutcome Outcome)>> InstallAsync(
        IReadOnlyList<UpdateItem> items,
        IProgress<UpdateProgress> progress,
        CancellationToken ct)
    {
        var results = new Dictionary<string, (UpdateItem Item, UpdateOutcome Outcome)>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, UpdateItem>(StringComparer.OrdinalIgnoreCase);

        // Only GUIDs reach the script, so nothing read from Windows Update is ever run as code.
        foreach (var item in items)
        {
            if (Guid.TryParse(item.Key, out var id))
            {
                byId[id.ToString()] = item;
            }
            else
            {
                results[item.Key] = (item, UpdateOutcome.Fail("Its update ID could not be read, so it was not installed."));
            }
        }

        if (byId.Count == 0)
        {
            return results.Values.ToList();
        }

        var wanted = string.Join(", ", byId.Keys.Select(k => $"'{k}'"));

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $wanted = @({{wanted}})
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $session.ClientApplicationID = 'Handheld Optimiser'
                Write-Output 'Searching Windows Update again for the chosen updates'
                $found = @($session.CreateUpdateSearcher().Search('IsInstalled=0').Updates |
                    Where-Object { $wanted -contains $_.Identity.UpdateID })
            } catch {
                foreach ($id in $wanted) { "{{Tag}}DONE`t$id`tfailed`t$('0x{0:X8}' -f $_.Exception.HResult)`t0" }
                exit 1
            }

            foreach ($id in $wanted) {
                if (-not ($found | Where-Object { $_.Identity.UpdateID -eq $id })) { "{{Tag}}DONE`t$id`tgone`t0`t0" }
            }

            foreach ($u in $found) {
                $id = $u.Identity.UpdateID
                try {
                    Write-Output "Downloading: $($u.Title)"
                    "{{Tag}}STEP`t$id`tDownloading…"
                    if (-not $u.EulaAccepted) { $u.AcceptEula() }
                    $one = New-Object -ComObject Microsoft.Update.UpdateColl
                    [void]$one.Add($u)

                    if (-not $u.IsDownloaded) {
                        $downloader = $session.CreateUpdateDownloader()
                        $downloader.Updates = $one
                        $download = $downloader.Download()
                        if ($download.ResultCode -ne 2 -and $download.ResultCode -ne 3) {
                            "{{Tag}}DONE`t$id`tfailed`t$('0x{0:X8}' -f $download.GetUpdateResult(0).HResult)`t0"
                            continue
                        }
                    }

                    Write-Output "Installing: $($u.Title)"
                    "{{Tag}}STEP`t$id`tInstalling…"
                    $installer = $session.CreateUpdateInstaller()
                    $installer.Updates = $one
                    $install = $installer.Install()
                    $result = $install.GetUpdateResult(0)
                    $state = 'failed'
                    if ($result.ResultCode -eq 2 -or $result.ResultCode -eq 3) { $state = 'ok' }
                    "{{Tag}}DONE`t$id`t$state`t$('0x{0:X8}' -f $result.HResult)`t$([int]$result.RebootRequired)"
                } catch {
                    "{{Tag}}DONE`t$id`tfailed`t$('0x{0:X8}' -f $_.Exception.HResult)`t0"
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
            if (parts.Length < 3 || !byId.TryGetValue(parts[1], out var item))
            {
                return;
            }

            if (parts[0] == Tag + "STEP")
            {
                progress.Report(new UpdateProgress(item, parts[2]));
            }
            else if (parts[0] == Tag + "DONE" && parts.Length >= 5)
            {
                var outcome = parts[2] switch
                {
                    "ok" => UpdateOutcome.Ok(parts[4] == "1" ? "Installed. Restart to finish." : "Installed.", parts[4] == "1"),
                    "gone" => UpdateOutcome.Current("Already installed, or no longer offered."),
                    _ => UpdateOutcome.Fail(Explain(parts[3]) ?? $"It failed to install ({parts[3]}).")
                };

                lock (results)
                {
                    results[item.Key] = (item, outcome);
                }

                progress.Report(new UpdateProgress(item, outcome.Message, outcome));
            }
        }

        _log.Info($"=== Installing {byId.Count} Windows update(s) ===");
        var run = await _runner.RunScriptAsync(
            script, "Install Windows updates", ct, echoScript: false, logOutput: false, onOutputLine: OnLine);

        lock (results)
        {
            // A script that died part way leaves some updates without a result line.
            foreach (var item in byId.Values.Where(i => !results.ContainsKey(i.Key)))
            {
                var outcome = UpdateOutcome.Fail($"Windows Update stopped before reaching it (exit {run.ExitCode}).");
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

    /// <summary>Plain words for the Windows Update errors someone is likely to hit on a handheld.</summary>
    private static string? Explain(string code) => code.ToUpperInvariant() switch
    {
        "0X80240016" => "Windows Update is already installing something. Try again when it finishes.",
        "0X80070422" => "The Windows Update service is turned off.",
        "0X8024402C" or "0X80072EE7" or "0X80072EFD" or "0X80072F8F" or "0X8024401C" => "Windows Update could not be reached. Check the internet connection.",
        "0X80240438" or "0X80244022" => "Windows Update's servers are busy. Try again later.",
        "0X80070070" => "Not enough disk space.",
        "0X8024A000" or "0X8024001E" => "Windows Update is shutting down or restarting. Try again in a minute.",
        _ => null
    };
}
