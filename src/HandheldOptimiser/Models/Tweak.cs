using HandheldOptimiser.Services;

namespace HandheldOptimiser.Models;

/// <summary>
/// Services a tweak needs in order to do its work. Passed in rather than resolved statically so a tweak
/// definition stays a plain description of intent.
/// </summary>
public sealed class TweakContext(LogService log, RegistryHelper registry, PowerShellRunner runner)
{
    public LogService Log { get; } = log;
    public RegistryHelper Registry { get; } = registry;
    public PowerShellRunner Runner { get; } = runner;
}

/// <summary>
/// One user-facing toggle.
///
/// A tweak is any combination of registry values and script work. Most are pure registry, a few
/// (optional features, boot configuration, scheduled tasks) need a script, and some need both, so
/// rather than a class per flavour this holds both and skips whichever half is empty.
/// </summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required TweakCategory Category { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public bool RequiresReboot { get; init; }

    /// <summary>Shown prominently in the UI for anything that lowers security or breaks a feature.</summary>
    public string? Warning { get; init; }

    /// <summary>Whether the One-Click Optimise run includes this tweak.</summary>
    public bool IncludeInOneClick { get; init; } = true;

    public IReadOnlyList<RegistryValueSpec> RegistryValues { get; init; } = [];

    /// <summary>
    /// Registry values the script half writes itself (and journals), rather than through
    /// <see cref="RegistryValues"/>. Listed so revert knows it may put them back.
    /// </summary>
    public IReadOnlyList<RegistryValueSpec> ScriptRegistryValues { get; init; } = [];

    /// <summary>
    /// Whether this tweak ever writes the given value. Revert restores only those, whatever the journal
    /// says, so undo data can never be used to write anywhere else with administrator rights.
    /// </summary>
    public bool OwnsRegistryValue(RegistryValueSnapshot snapshot) =>
        RegistryValues.Concat(ScriptRegistryValues).Any(spec =>
            spec.Root == snapshot.Root &&
            string.Equals(spec.SubKey, snapshot.SubKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(spec.ValueName, snapshot.ValueName, StringComparison.OrdinalIgnoreCase));

    public Func<TweakContext, CancellationToken, Task<TweakState>>? ScriptDetect { get; init; }
    public Func<TweakContext, TweakJournalEntry, CancellationToken, Task<TweakResult>>? ScriptApply { get; init; }
    public Func<TweakContext, TweakJournalEntry, CancellationToken, Task<TweakResult>>? ScriptRevert { get; init; }

    public async Task<TweakState> DetectAsync(TweakContext ctx, CancellationToken ct)
    {
        var states = new List<TweakState>();

        if (RegistryValues.Count > 0)
        {
            var matched = RegistryValues.Count(ctx.Registry.ValueMatches);
            states.Add(matched == RegistryValues.Count ? TweakState.Applied
                : matched == 0 ? TweakState.NotApplied
                : TweakState.Partial);
        }

        if (ScriptDetect is not null)
        {
            try
            {
                states.Add(await ScriptDetect(ctx, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Log.Warning($"Detection failed for \"{Name}\": {ex.Message}");
                states.Add(TweakState.Unknown);
            }
        }

        if (states.Count == 0)
        {
            return TweakState.Unknown;
        }

        if (states.All(s => s == TweakState.Applied))
        {
            return TweakState.Applied;
        }

        if (states.All(s => s == TweakState.NotApplied))
        {
            return TweakState.NotApplied;
        }

        return states.Contains(TweakState.Unknown) && states.All(s => s is TweakState.Unknown or TweakState.NotApplied)
            ? TweakState.Unknown
            : TweakState.Partial;
    }

    public async Task<(TweakResult Result, TweakJournalEntry Journal)> ApplyAsync(TweakContext ctx, CancellationToken ct)
    {
        var journal = new TweakJournalEntry
        {
            TweakId = Id,
            TweakName = Name,
            AppliedAtUtc = DateTimeOffset.UtcNow
        };

        ctx.Log.Info($"Applying: {Name}");

        var anyFailed = false;
        var anyChanged = false;

        foreach (var spec in RegistryValues)
        {
            ct.ThrowIfCancellationRequested();

            if (ctx.Registry.ValueMatches(spec))
            {
                ctx.Log.Trace($"    {spec.DisplayPath} already set to {spec.DesiredValue}");
                continue;
            }

            var snapshot = ctx.Registry.WriteValue(spec);
            if (snapshot is null)
            {
                anyFailed = true;
                continue;
            }

            journal.RegistrySnapshots.Add(snapshot);
            anyChanged = true;
        }

        if (ScriptApply is not null)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var scriptResult = await ScriptApply(ctx, journal, ct);

                if (scriptResult.IsFailure)
                {
                    anyFailed = true;
                }
                else if (scriptResult.Status == ResultStatus.Success)
                {
                    anyChanged = true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Log.Error($"\"{Name}\" threw: {ex.Message}");
                anyFailed = true;
            }
        }

        var result = anyFailed
            ? TweakResult.Fail(Id, $"\"{Name}\" did not fully apply. See log.")
            : anyChanged
                ? TweakResult.Ok(Id, $"\"{Name}\" applied.", RequiresReboot)
                : TweakResult.NoChange(Id, $"\"{Name}\" was already applied.");

        return (result, journal);
    }

    public async Task<TweakResult> RevertAsync(TweakContext ctx, TweakJournalEntry journal, CancellationToken ct)
    {
        ctx.Log.Info($"Reverting: {Name}");

        var anyFailed = false;

        if (ScriptRevert is not null)
        {
            try
            {
                var scriptResult = await ScriptRevert(ctx, journal, ct);
                if (scriptResult.IsFailure)
                {
                    anyFailed = true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Log.Error($"Revert of \"{Name}\" threw: {ex.Message}");
                anyFailed = true;
            }
        }

        foreach (var snapshot in journal.RegistrySnapshots)
        {
            ct.ThrowIfCancellationRequested();

            // Refused rather than counted as a failure: nothing this tweak changed is left behind, and a
            // failure would keep the bad entry in the journal and fail every revert after it.
            if (!OwnsRegistryValue(snapshot))
            {
                ctx.Log.Error($"BLOCKED restore of {snapshot.Root}\\{snapshot.SubKey}\\{snapshot.ValueName}: " +
                              $"\"{Name}\" never writes that value, so its undo data should not contain it.");
                continue;
            }

            if (!ctx.Registry.RestoreSnapshot(snapshot))
            {
                anyFailed = true;
            }
        }

        return anyFailed
            ? TweakResult.Fail(Id, $"\"{Name}\" did not fully revert. See log.")
            : TweakResult.Ok(Id, $"\"{Name}\" reverted.", RequiresReboot);
    }
}
