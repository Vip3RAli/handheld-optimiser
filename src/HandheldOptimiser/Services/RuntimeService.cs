using System.IO;
using HandheldOptimiser.Models;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.Services;

public sealed class RuntimeScanResult
{
    public required RuntimePackage Package { get; init; }
    public RuntimeStatus Status { get; init; }
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// Scans for and installs game runtimes through winget. winget handles the download, verifies the
/// installer hash against its manifest, and runs the vendor installer silently; this class only decides
/// what to ask for and reports what came back.
/// </summary>
public sealed class RuntimeService(LogService log, PowerShellRunner runner, RestorePointService restorePoints)
{
    // winget exit codes (HRESULTs, so negative as Int32).
    private const int WingetNoPackageFound = unchecked((int)0x8A150014);
    private const int WingetUpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int WingetAlreadyInstalled = unchecked((int)0x8A150061);
    private const int WingetRebootToFinish = unchecked((int)0x8A150109);

    // Installer exit codes meaning "succeeded, restart needed".
    private const int MsiRebootRequired = 3010;
    private const int MsiRebootInitiated = 1641;

    /// <summary>winget lookups are ~0.7 s each and mostly waiting, so a few run side by side.</summary>
    private const int ScanParallelism = 4;

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;
    private readonly RestorePointService _restorePoints = restorePoints;

    public IReadOnlyList<RuntimePackage> Catalog => RuntimeCatalog.All;

    public async Task<IReadOnlyList<RuntimeScanResult>> ScanAsync(
        IReadOnlyList<RuntimePackage> packages,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        _log.Command($"Scan {packages.Count} game runtime(s) with winget");

        using var gate = new SemaphoreSlim(ScanParallelism);
        var done = 0;

        var tasks = packages.Select(async package =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await ScanOneAsync(package, ct);
                progress?.Report($"Checked {Interlocked.Increment(ref done)} of {packages.Count}: {package.Name}");
                return result;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            var detail = r.Status switch
            {
                RuntimeStatus.UpToDate => $"up to date ({r.InstalledVersion})",
                RuntimeStatus.UpdateAvailable => $"{r.InstalledVersion} installed, {r.LatestVersion} available",
                RuntimeStatus.Missing => "not installed",
                _ => $"could not check ({r.Note})"
            };
            _log.Trace($"    {r.Package.Name}: {detail}");
        }

        var updates = results.Count(r => r.Status == RuntimeStatus.UpdateAvailable);
        var missing = results.Count(r => r.Status == RuntimeStatus.Missing);
        _log.Info($"Runtime scan finished: {updates} update(s) available, {missing} not installed.");

        return results;
    }

    private async Task<RuntimeScanResult> ScanOneAsync(RuntimePackage package, CancellationToken ct)
    {
        switch (package.Detection)
        {
            case RuntimeDetection.Files:
            {
                var present = package.DetectFiles.All(f => File.Exists(Environment.ExpandEnvironmentVariables(f)));
                return new RuntimeScanResult
                {
                    Package = package,
                    Status = present ? RuntimeStatus.UpToDate : RuntimeStatus.Missing,
                    InstalledVersion = present ? package.FixedVersionLabel : null,
                    LatestVersion = package.FixedVersionLabel
                };
            }

            case RuntimeDetection.NetFx3:
            {
                var install = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", "Install", null);
                var present = install is int i && i == 1;
                return new RuntimeScanResult
                {
                    Package = package,
                    Status = present ? RuntimeStatus.UpToDate : RuntimeStatus.Missing,
                    InstalledVersion = present ? package.FixedVersionLabel : null,
                    LatestVersion = package.FixedVersionLabel
                };
            }

            case RuntimeDetection.DotNet:
                return await ScanDotNetAsync(package, ct);

            default:
                return await ScanWithWingetAsync(package, ct);
        }
    }

    private async Task<RuntimeScanResult> ScanDotNetAsync(RuntimePackage package, CancellationToken ct)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", package.DotNetFramework!);

        // Apps roll forward to the newest patch of their major version, so the newest folder is the one
        // that matters. Older patches left beside it are harmless.
        var installed = Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(v => v.StartsWith(package.DotNetMajor + ".", StringComparison.Ordinal))
                .OrderByDescending(v => v, VersionComparer.Instance)
                .FirstOrDefault()
            : null;

        if (installed is null)
        {
            return new RuntimeScanResult { Package = package, Status = RuntimeStatus.Missing };
        }

        var latest = await GetLatestWingetVersionAsync(package.Id, ct);

        if (latest is null)
        {
            return new RuntimeScanResult
            {
                Package = package,
                Status = RuntimeStatus.Error,
                InstalledVersion = installed,
                Note = "could not read the latest version from winget"
            };
        }

        var outdated = VersionComparer.Instance.Compare(latest, installed) > 0;

        return new RuntimeScanResult
        {
            Package = package,
            Status = outdated ? RuntimeStatus.UpdateAvailable : RuntimeStatus.UpToDate,
            InstalledVersion = installed,
            LatestVersion = outdated ? latest : installed
        };
    }

    /// <summary>
    /// Reads the version from <c>winget show</c>. The label is localised, so this matches the first
    /// "label: 1.2.3" line by shape rather than by the word "Version".
    /// </summary>
    private async Task<string?> GetLatestWingetVersionAsync(string id, CancellationToken ct)
    {
        var outcome = await _runner.RunProcessAsync(
            "winget.exe",
            ["show", "--id", id, "--exact", "--accept-source-agreements", "--disable-interactivity"],
            $"winget show {id}",
            ct,
            echoCommand: false,
            logOutput: false);

        if (!outcome.Succeeded)
        {
            return null;
        }

        foreach (var line in outcome.OutputLines.Select(l => l.Split('\r')[^1].Trim()))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^[^:]+:\s*(\d+(?:\.\d+)+)$");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private async Task<RuntimeScanResult> ScanWithWingetAsync(RuntimePackage package, CancellationToken ct)
    {
        var outcome = await _runner.RunProcessAsync(
            "winget.exe",
            ["list", "--id", package.Id, "--exact", "--accept-source-agreements", "--disable-interactivity"],
            $"winget list {package.Id}",
            ct,
            echoCommand: false,
            logOutput: false);

        if (outcome.ExitCode == WingetNoPackageFound)
        {
            return new RuntimeScanResult { Package = package, Status = RuntimeStatus.Missing };
        }

        if (!outcome.Succeeded)
        {
            return new RuntimeScanResult
            {
                Package = package,
                Status = RuntimeStatus.Error,
                Note = outcome.ExitCode == -1 ? "winget is not available" : $"winget exit 0x{outcome.ExitCode:X8}"
            };
        }

        var rows = ParseListRows(outcome.StdOut, package.Id);

        if (rows.Count == 0)
        {
            return new RuntimeScanResult { Package = package, Status = RuntimeStatus.Missing };
        }

        // Several side-by-side versions can be registered under one ID (.NET patch releases, for example).
        // The newest one decides whether anything is actually out of date.
        var installed = rows.Select(r => r.Version).OrderByDescending(v => v, VersionComparer.Instance).First();
        var available = rows.Select(r => r.Available).OfType<string>().Where(a => a.Length > 0)
            .OrderByDescending(v => v, VersionComparer.Instance).FirstOrDefault();

        var outdated = available is not null && VersionComparer.Instance.Compare(available, installed) > 0;

        return new RuntimeScanResult
        {
            Package = package,
            Status = outdated ? RuntimeStatus.UpdateAvailable : RuntimeStatus.UpToDate,
            InstalledVersion = installed,
            LatestVersion = outdated ? available : installed
        };
    }

    /// <summary>
    /// Parses winget's table by the header's column positions. Names can be truncated with an ellipsis,
    /// but winget always keeps the columns aligned to the header, so positions are reliable where
    /// splitting on whitespace is not (names contain spaces, and the Available column is often blank).
    /// </summary>
    private static List<(string Version, string? Available)> ParseListRows(string stdout, string id)
    {
        var lines = stdout.Split('\n')
            .Select(l => l.TrimEnd('\r').Split('\r')[^1])
            .ToList();

        var headerIndex = lines.FindIndex(l => l.Contains(" Id ", StringComparison.Ordinal) &&
                                               l.Contains("Version", StringComparison.Ordinal));
        var rows = new List<(string, string?)>();

        if (headerIndex < 0)
        {
            return rows;
        }

        var header = lines[headerIndex];
        var idCol = header.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        var versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        var availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceCol = header.IndexOf("Source", StringComparison.Ordinal);

        string Cell(string line, int start, int end) =>
            start < 0 || start >= line.Length ? string.Empty
            : line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim();

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (line.Length <= versionCol || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var rowId = Cell(line, idCol, versionCol);
            if (!rowId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionEnd = availableCol >= 0 ? availableCol : sourceCol;
            var version = Cell(line, versionCol, versionEnd).TrimStart('<', ' ');
            var available = availableCol >= 0 ? Cell(line, availableCol, sourceCol) : null;

            rows.Add((version, string.IsNullOrEmpty(available) ? null : available));
        }

        return rows;
    }

    /// <summary>
    /// Installs or updates each package. One restore point is taken first, under the same rule as the
    /// tweaks: if it cannot be created, nothing is installed.
    /// </summary>
    public async Task<ApplyRunSummary> InstallAsync(
        IReadOnlyList<RuntimePackage> packages,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var summary = new ApplyRunSummary();

        if (packages.Count == 0)
        {
            return summary;
        }

        _log.Info($"=== Installing or updating {packages.Count} runtime(s) ===");

        progress?.Report("Creating System Restore point…");
        var rp = await _restorePoints.CreateAsync("Handheld Optimiser: game runtimes", ct);
        summary.RestorePointCreated = rp.Created;

        if (!rp.Created)
        {
            summary.Aborted = true;
            summary.AbortReason = $"No System Restore point could be created ({rp.Message}). Nothing was installed.";
            _log.Error("ABORTED before installing anything: no restore point.");
            return summary;
        }

        for (var i = 0; i < packages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var package = packages[i];
            progress?.Report($"{i + 1} of {packages.Count}: {package.Name}");

            var result = package.Detection == RuntimeDetection.NetFx3
                ? await InstallNetFx3Async(package, ct)
                : await InstallWithWingetAsync(package, ct);

            summary.Results.Add(result);

            if (result.IsFailure)
            {
                _log.Error(result.Message ?? $"{package.Name} failed.");
            }
            else
            {
                _log.Success(result.Message ?? $"{package.Name} done.");
            }
        }

        _log.Info($"=== Finished: {summary.Applied} installed or updated, {summary.AlreadyDone} already current, " +
                  $"{summary.Failures} failed ===");

        if (summary.RebootRequired)
        {
            _log.Warning("A restart is required to finish installing some runtimes.");
        }

        return summary;
    }

    private async Task<TweakResult> InstallWithWingetAsync(RuntimePackage package, CancellationToken ct)
    {
        // "install" upgrades an existing installation in place, so one command covers both cases.
        var outcome = await _runner.RunProcessAsync(
            "winget.exe",
            [
                "install", "--id", package.Id, "--exact", "--source", "winget", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
            ],
            $"Install or update {package.Name} ({package.Id})",
            ct);

        return outcome.ExitCode switch
        {
            0 => TweakResult.Ok(package.Id, $"{package.Name} installed or updated."),
            MsiRebootRequired or MsiRebootInitiated or WingetRebootToFinish =>
                TweakResult.Ok(package.Id, $"{package.Name} installed; restart to finish.", rebootRequired: true),
            WingetUpdateNotApplicable or WingetAlreadyInstalled =>
                TweakResult.NoChange(package.Id, $"{package.Name} is already the latest version."),
            -1 => TweakResult.Fail(package.Id, "winget could not be started. Install \"App Installer\" from the Microsoft Store."),
            _ => TweakResult.Fail(package.Id, $"{package.Name} failed (winget exit 0x{outcome.ExitCode:X8}). See log.")
        };
    }

    private async Task<TweakResult> InstallNetFx3Async(RuntimePackage package, CancellationToken ct)
    {
        var outcome = await _runner.RunProcessAsync(
            "dism.exe",
            ["/Online", "/Enable-Feature", "/FeatureName:NetFx3", "/All", "/NoRestart"],
            "Enable .NET Framework 3.5 (downloads from Windows Update)",
            ct);

        return outcome.ExitCode switch
        {
            0 => TweakResult.Ok(package.Id, ".NET Framework 3.5 enabled."),
            MsiRebootRequired => TweakResult.Ok(package.Id, ".NET Framework 3.5 enabled; restart to finish.", rebootRequired: true),
            _ => TweakResult.Fail(package.Id, $".NET Framework 3.5 could not be enabled (DISM exit {outcome.ExitCode}). See log.")
        };
    }

    /// <summary>Compares dotted version strings numerically, so 12.0.40664 sorts above 12.0.30501.</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x);
            var b = Parts(y);

            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var pa = i < a.Length ? a[i] : 0;
                var pb = i < b.Length ? b[i] : 0;
                if (pa != pb)
                {
                    return pa.CompareTo(pb);
                }
            }

            return 0;
        }

        private static long[] Parts(string? version) =>
            (version ?? string.Empty)
                .Split('.', '-', '+')
                .Select(p => long.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
                .ToArray();
    }
}
