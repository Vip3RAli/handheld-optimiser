using System.Text.RegularExpressions;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Desktop apps that winget knows a newer version of, found with <c>winget upgrade</c> and updated one
/// at a time. winget downloads each installer, checks its hash against the manifest and runs it
/// silently; this class only picks which IDs to pass and reads back what happened.
/// </summary>
public sealed class WingetUpdates(LogService log, PowerShellRunner runner)
{
    private const int WingetNoPackageFound = unchecked((int)0x8A150014);
    private const int WingetUpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int WingetPackageInUse = unchecked((int)0x8A150101);
    private const int WingetInstallInProgress = unchecked((int)0x8A150102);
    private const int WingetFileInUse = unchecked((int)0x8A150103);
    private const int WingetDiskFull = unchecked((int)0x8A150105);
    private const int WingetNoNetwork = unchecked((int)0x8A150107);
    private const int WingetRebootToFinish = unchecked((int)0x8A150109);
    private const int WingetRebootInitiated = unchecked((int)0x8A15010B);
    private const int WingetCancelledByUser = unchecked((int)0x8A15010C);
    private const int MsiRebootRequired = 3010;
    private const int MsiRebootInitiated = 1641;

    private const string Ellipsis = "…";

    /// <summary>
    /// winget package IDs ("Valve.Steam", "Microsoft.VCRedist.2015+.x64") and source names. An ID goes
    /// into winget's arguments, so one starting with a dash would be read as an option; requiring a
    /// letter or digit first rules that out.
    /// </summary>
    private static readonly Regex IdShape = new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$", RegexOptions.CultureInvariant);

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    public async Task<UpdateScan> ScanAsync(CancellationToken ct)
    {
        var outcome = await _runner.RunProcessAsync(
            "winget.exe",
            ["upgrade", "--accept-source-agreements", "--disable-interactivity"],
            "Check for app updates with winget",
            ct,
            logOutput: false);

        if (outcome.ExitCode == -1)
        {
            return UpdateScan.Failed("winget is not available. Install \"App Installer\" from the Microsoft Store.");
        }

        if (!outcome.Succeeded && outcome.ExitCode != WingetNoPackageFound)
        {
            _log.Warning($"    winget exit 0x{outcome.ExitCode:X8}: {outcome.StdErr.Trim()}");
            return UpdateScan.Failed($"winget could not check for updates (exit 0x{outcome.ExitCode:X8}).");
        }

        var items = ParseUpgradeTable(outcome.StdOut)
            // Store apps have their own section, which updates them through the Store itself.
            .Where(i => !string.Equals(i.Detail, "msstore", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var item in items)
        {
            _log.Trace($"    {item.Name} ({item.Key}): {item.InstalledVersion} → {item.AvailableVersion}");
        }

        _log.Info($"winget: {items.Count} app update(s) available.");
        return new UpdateScan(items);
    }

    /// <summary>
    /// Reads the table <c>winget upgrade</c> prints. The header is translated, so the columns are found by
    /// position rather than by name: Name, Id, Version, Available and, when any row has one, Source. Each
    /// starts where a header word starts, and winget pads every row to those same positions.
    ///
    /// Reading stops at the first line that is not a valid row. That is the "N upgrades available" line
    /// under the table, and it keeps out the second table winget prints for pinned packages, which the
    /// user has asked winget to leave alone.
    /// </summary>
    public static IReadOnlyList<UpdateItem> ParseUpgradeTable(string stdout)
    {
        var items = new List<UpdateItem>();

        // Progress spinners redraw the same line with carriage returns; only the last frame is real.
        var lines = stdout.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Split('\r')[^1].TrimEnd())
            .ToList();

        var separator = lines.FindIndex(l => l.Length >= 10 && l.All(c => c == '-'));
        if (separator < 1)
        {
            return items;
        }

        var header = lines[separator - 1];
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' '))
            {
                starts.Add(i);
            }
        }

        if (starts.Count is not (4 or 5))
        {
            return items;
        }

        string Cell(string line, int column)
        {
            var start = starts[column];
            var end = column + 1 < starts.Count ? starts[column + 1] : line.Length;
            return start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)].Trim();
        }

        foreach (var line in lines.Skip(separator + 1))
        {
            if (line.Length <= starts[3])
            {
                break;
            }

            var name = Cell(line, 0);
            var id = Cell(line, 1);
            var version = Cell(line, 2);
            var available = Cell(line, 3);
            var source = starts.Count == 5 ? Cell(line, 4) : string.Empty;

            if (name.Length == 0 || version.Length == 0 || available.Length == 0 ||
                !IdShape.IsMatch(id.EndsWith(Ellipsis, StringComparison.Ordinal) ? id[..^1] : id) ||
                (source.Length > 0 && !IdShape.IsMatch(source)))
            {
                break;
            }

            items.Add(new UpdateItem(UpdateSource.Winget, id, name, version, available, source.Length > 0 ? source : null));
        }

        return items;
    }

    /// <summary>Updates each item in turn, reporting as each one starts and finishes.</summary>
    public async Task<IReadOnlyList<(UpdateItem Item, UpdateOutcome Outcome)>> InstallAsync(
        IReadOnlyList<UpdateItem> items,
        IProgress<UpdateProgress> progress,
        CancellationToken ct)
    {
        var results = new List<(UpdateItem, UpdateOutcome)>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new UpdateProgress(item, "Updating…"));

            var outcome = await InstallOneAsync(item, ct);
            results.Add((item, outcome));
            progress.Report(new UpdateProgress(item, outcome.Message, outcome));

            if (outcome.Kind == UpdateOutcomeKind.Failed)
            {
                _log.Error($"{item.Name}: {outcome.Message}");
            }
            else
            {
                _log.Success($"{item.Name}: {outcome.Message}");
            }
        }

        return results;
    }

    private async Task<UpdateOutcome> InstallOneAsync(UpdateItem item, CancellationToken ct)
    {
        // winget cuts long IDs short with an ellipsis when its table is too wide. The full ID cannot be
        // recovered from the table, so a cut one is passed without --exact: winget then matches it as part
        // of an ID among installed packages only, and refuses rather than guesses if several match.
        var truncated = item.Key.EndsWith(Ellipsis, StringComparison.Ordinal);
        var id = truncated ? item.Key[..^1] : item.Key;

        if (!IdShape.IsMatch(id))
        {
            return UpdateOutcome.Fail("Its winget ID could not be read, so it was not updated.");
        }

        List<string> args = ["upgrade", "--id", id];
        if (!truncated)
        {
            args.Add("--exact");
        }

        if (item.Detail is { } source && IdShape.IsMatch(source))
        {
            args.AddRange(["--source", source]);
        }

        args.AddRange(["--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"]);

        var outcome = await _runner.RunProcessAsync("winget.exe", args, $"Update {item.Name} ({id})", ct);

        return outcome.ExitCode switch
        {
            0 => UpdateOutcome.Ok($"Updated to {item.AvailableVersion}."),
            MsiRebootRequired or MsiRebootInitiated or WingetRebootToFinish or WingetRebootInitiated =>
                UpdateOutcome.Ok("Updated. Restart to finish.", rebootRequired: true),
            WingetUpdateNotApplicable => UpdateOutcome.Current("Already up to date."),
            WingetNoPackageFound => UpdateOutcome.Fail("winget could not find it any more. Check again."),
            WingetPackageInUse or WingetFileInUse => UpdateOutcome.Fail("It's open. Close it and try again."),
            WingetInstallInProgress => UpdateOutcome.Fail("Another install is running. Try again when it finishes."),
            WingetDiskFull => UpdateOutcome.Fail("Not enough disk space."),
            WingetNoNetwork => UpdateOutcome.Fail("No internet connection."),
            WingetCancelledByUser => UpdateOutcome.Fail("The installer was cancelled."),
            -1 => UpdateOutcome.Fail("winget could not be started."),
            _ => UpdateOutcome.Fail($"The update failed (winget exit 0x{outcome.ExitCode:X8}). The console has the detail.")
        };
    }
}
