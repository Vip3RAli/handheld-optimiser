using HandheldOptimiser.Models;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.Services;

public sealed class ApplyRunSummary
{
    public List<TweakResult> Results { get; } = [];
    public bool RestorePointCreated { get; set; }
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }

    public bool RebootRequired => Results.Any(r => r.RebootRequired && r.Status == ResultStatus.Success);
    public int Applied => Results.Count(r => r.Status == ResultStatus.Success);
    public int AlreadyDone => Results.Count(r => r.Status == ResultStatus.NoChangeNeeded);
    public int Failures => Results.Count(r => r.IsFailure);
}

/// <summary>
/// Orchestrates everything: restore point, then tweaks, then journalling.
///
/// The ordering matters and is not negotiable — a restore point that is created after the first registry
/// write is worse than useless, because it would snapshot the already-modified machine.
/// </summary>
public sealed class TweakEngine
{
    private readonly LogService _log;
    private readonly RestorePointService _restorePoints;
    private readonly TweakJournalService _journal;
    private readonly TweakContext _context;

    public IReadOnlyList<Tweak> AllTweaks { get; }

    public TweakEngine(
        LogService log,
        RegistryHelper registry,
        PowerShellRunner runner,
        RestorePointService restorePoints,
        TweakJournalService journal)
    {
        _log = log;
        _restorePoints = restorePoints;
        _journal = journal;
        _context = new TweakContext(log, registry, runner);

        AllTweaks =
        [
            .. GamingTweaks.All,
            .. DebloatTweaks.All,
            .. CpuKernelTweaks.All,
            .. NetworkTweaks.All,
            .. ServiceTweaks.All,
            .. InterfaceTweaks.All
        ];

        PowerActions = TweakDefinitions.PowerActions.All;
    }

    public IReadOnlyList<PowerAction> PowerActions { get; }

    public TweakContext Context => _context;

    public Tweak? FindById(string id) => AllTweaks.FirstOrDefault(t => t.Id == id);

    public IEnumerable<Tweak> DragCarTweaks => AllTweaks.Where(t => t.IncludeInDragCar);

    public async Task<TweakState> DetectAsync(Tweak tweak, CancellationToken ct = default) =>
        await tweak.DetectAsync(_context, ct);

    /// <summary>
    /// Applies a set of tweaks, creating one restore point first.
    /// </summary>
    /// <param name="createRestorePoint">
    /// When true (the default), the run aborts if no checkpoint could be made. Passing false is only for
    /// a user who has explicitly acknowledged running without a safety net.
    /// </param>
    public async Task<ApplyRunSummary> ApplyAsync(
        IReadOnlyList<Tweak> tweaks,
        string runDescription,
        bool createRestorePoint = true,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ApplyRunSummary();

        if (tweaks.Count == 0)
        {
            _log.Warning("Nothing selected, nothing to do.");
            return summary;
        }

        _log.Info($"=== {runDescription}: {tweaks.Count} tweak(s) selected ===");

        if (createRestorePoint)
        {
            progress?.Report("Creating System Restore point…");

            var rp = await _restorePoints.CreateAsync($"Handheld Optimiser — {runDescription}", ct);
            summary.RestorePointCreated = rp.Created;

            if (!rp.Created)
            {
                summary.Aborted = true;
                summary.AbortReason =
                    $"No System Restore point could be created ({rp.Message}). Nothing was changed. " +
                    "Enable System Protection in Windows settings, or re-run and explicitly choose to " +
                    "continue without a restore point.";

                _log.Error("ABORTED before making any change: no restore point.");
                return summary;
            }
        }
        else
        {
            _log.Warning("Proceeding WITHOUT a restore point at the user's explicit request.");
        }

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(tweak.Name);

            var (result, journalEntry) = await tweak.ApplyAsync(_context, ct);
            summary.Results.Add(result);

            // Only journal a run that actually changed something, otherwise we would record a
            // no-op entry whose snapshots are the already-optimised values.
            if (result.Status == ResultStatus.Success &&
                (journalEntry.RegistrySnapshots.Count > 0 || journalEntry.CapturedState.Count > 0))
            {
                _journal.Record(journalEntry);
            }

            switch (result.Status)
            {
                case ResultStatus.Success:
                    _log.Success(result.Message ?? $"{tweak.Name} applied.");
                    break;
                case ResultStatus.NoChangeNeeded:
                    _log.Info(result.Message ?? $"{tweak.Name} was already applied.");
                    break;
                default:
                    _log.Error(result.Message ?? $"{tweak.Name} failed.");
                    break;
            }
        }

        _log.Info($"=== Finished: {summary.Applied} applied, {summary.AlreadyDone} already set, " +
                  $"{summary.Failures} failed ===");

        if (summary.RebootRequired)
        {
            _log.Warning("A restart is required before some of these changes take effect.");
        }

        return summary;
    }

    /// <summary>
    /// Reverts a tweak using the snapshot recorded when it was applied. A tweak with no journal entry
    /// cannot be reverted, which is reported rather than silently no-oping.
    /// </summary>
    public async Task<TweakResult> RevertAsync(Tweak tweak, CancellationToken ct = default)
    {
        var entry = _journal.Find(tweak.Id);

        if (entry is null)
        {
            var message = $"No undo data recorded for \"{tweak.Name}\" — it was not applied by this app, " +
                          "so there is no original value to restore.";
            _log.Warning(message);
            return TweakResult.Blocked(tweak.Id, message);
        }

        var result = await tweak.RevertAsync(_context, entry, ct);

        if (!result.IsFailure)
        {
            _journal.Remove(tweak.Id);
        }

        return result;
    }

    public async Task<ApplyRunSummary> RevertAllAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ApplyRunSummary();
        var entries = _journal.All().ToList();

        if (entries.Count == 0)
        {
            _log.Info("Nothing to revert — the undo journal is empty.");
            return summary;
        }

        _log.Info($"=== Reverting {entries.Count} previously applied tweak(s) ===");

        // Reverse order so tweaks undo in the opposite sequence to how they were applied.
        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();

            var tweak = FindById(entry.TweakId);
            if (tweak is null)
            {
                _log.Warning($"Journal references unknown tweak \"{entry.TweakId}\"; skipping.");
                continue;
            }

            progress?.Report(tweak.Name);
            summary.Results.Add(await RevertAsync(tweak, ct));
        }

        _log.Info($"=== Revert finished: {summary.Applied} reverted, {summary.Failures} failed ===");
        return summary;
    }

    public bool HasUndoData => _journal.All().Count > 0;

    /// <summary>
    /// Runs a one-shot maintenance action. Actions that change system configuration take a restore point
    /// first under the same rule as tweaks: no checkpoint, no change. Nothing is journalled, because there
    /// is no prior state to return to.
    /// </summary>
    public async Task<ApplyRunSummary> RunActionAsync(
        PowerAction action,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new ApplyRunSummary();

        _log.Info($"=== {action.Name} ===");

        if (action.CreateRestorePoint)
        {
            progress?.Report("Creating System Restore point…");

            var rp = await _restorePoints.CreateAsync($"Handheld Optimiser — {action.Name}", ct);
            summary.RestorePointCreated = rp.Created;

            if (!rp.Created)
            {
                summary.Aborted = true;
                summary.AbortReason =
                    $"No System Restore point could be created ({rp.Message}). Nothing was changed.";
                _log.Error("ABORTED before making any change: no restore point.");
                return summary;
            }
        }

        TweakResult result;
        try
        {
            result = await action.Execute(_context, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = TweakResult.Fail(action.Id, $"\"{action.Name}\" threw: {ex.Message}");
        }

        summary.Results.Add(result);

        if (result.IsFailure)
        {
            _log.Error(result.Message ?? $"{action.Name} failed.");
        }
        else
        {
            _log.Success(result.Message ?? $"{action.Name} finished.");
        }

        if (summary.RebootRequired)
        {
            _log.Warning("A restart is required before this takes full effect.");
        }

        return summary;
    }
}
