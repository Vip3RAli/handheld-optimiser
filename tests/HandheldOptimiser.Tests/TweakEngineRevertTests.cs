using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Microsoft.Win32;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The undo path: what <see cref="TweakEngine"/> restores, what it refuses to, and when it keeps or drops
/// the journal entry. Runs against <see cref="FakeRegistry"/> and <see cref="FakeJournal"/>, so nothing
/// on the machine is read or written.
/// </summary>
public sealed class TweakEngineRevertTests
{
    private const string SubKey = @"SOFTWARE\HandheldOptimiserTests";

    private readonly FakeRegistry _registry = new();
    private readonly FakeJournal _journal = new();
    private readonly TweakEngine _engine;

    public TweakEngineRevertTests()
    {
        var log = TestLog.Instance;
        var runner = new PowerShellRunner(log);

        // Never called: every apply here passes createRestorePoint: false.
        var restorePoints = new RestorePointService(log, runner, new RegistryHelper(log));

        _engine = new TweakEngine(log, _registry, runner, restorePoints, _journal, HandheldDevice.RogAlly);
    }

    private static RegistryValueSpec Spec(string valueName, int desired = 0) =>
        new(RegistryRoot.CurrentUser, SubKey, valueName, desired);

    private static Tweak MakeTweak(
        string id,
        IReadOnlyList<RegistryValueSpec>? values = null,
        Func<TweakContext, TweakJournalEntry, CancellationToken, Task<TweakResult>>? scriptRevert = null) => new()
    {
        Id = id,
        Name = id,
        Description = "Test tweak",
        Category = TweakCategory.Gaming,
        RegistryValues = values ?? [],
        ScriptRevert = scriptRevert
    };

    private static RegistryValueSnapshot SnapshotOf(RegistryValueSpec spec, int? original) => new()
    {
        Root = spec.Root,
        SubKey = spec.SubKey,
        ValueName = spec.ValueName,
        KeyExisted = original is not null,
        ValueExisted = original is not null,
        OriginalValue = original?.ToString(),
        OriginalKind = original is null ? RegistryValueKind.Unknown : RegistryValueKind.DWord
    };

    private static TweakJournalEntry EntryFor(string tweakId, params RegistryValueSnapshot[] snapshots) => new()
    {
        TweakId = tweakId,
        TweakName = tweakId,
        RegistrySnapshots = [.. snapshots]
    };

    [Fact]
    public async Task Revert_without_undo_data_is_blocked_and_changes_nothing()
    {
        var tweak = MakeTweak("test.nodata", [Spec("A")]);

        var result = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Blocked, result.Status);
        Assert.Empty(_registry.Restored);
    }

    [Fact]
    public async Task Revert_restores_the_snapshot_and_clears_the_journal_entry()
    {
        var spec = Spec("A");
        var tweak = MakeTweak("test.restore", [spec]);
        _journal.Record(EntryFor(tweak.Id, SnapshotOf(spec, original: 1)));

        var result = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Single(_registry.Restored);
        Assert.Equal(1, _registry.ReadValue(spec.Root, spec.SubKey, spec.ValueName));
        Assert.Null(_journal.Find(tweak.Id));
    }

    [Fact]
    public async Task Revert_refuses_a_snapshot_for_a_value_the_tweak_does_not_own()
    {
        var owned = Spec("Owned");
        var tweak = MakeTweak("test.foreign", [owned]);

        // Planted undo data pointing somewhere this tweak never writes.
        var foreign = new RegistryValueSnapshot
        {
            Root = RegistryRoot.LocalMachine,
            SubKey = @"SYSTEM\CurrentControlSet\Services\SomethingElse",
            ValueName = "Start",
            ValueExisted = true,
            OriginalValue = "4",
            OriginalKind = RegistryValueKind.DWord
        };
        _journal.Record(EntryFor(tweak.Id, foreign));

        var result = await _engine.RevertAsync(tweak);

        Assert.Empty(_registry.Restored);
        Assert.False(result.IsFailure);
        Assert.Null(_journal.Find(tweak.Id));
    }

    [Fact]
    public async Task Revert_that_fails_keeps_the_journal_entry_for_another_try()
    {
        var spec = Spec("A");
        var tweak = MakeTweak("test.fails", [spec]);
        _journal.Record(EntryFor(tweak.Id, SnapshotOf(spec, original: 1)));
        _registry.FailRestores = true;

        var result = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.NotNull(_journal.Find(tweak.Id));
    }

    [Fact]
    public async Task Revert_hands_the_journal_entry_to_the_script_half()
    {
        TweakJournalEntry? seen = null;
        var tweak = MakeTweak("test.script", scriptRevert: (_, entry, _) =>
        {
            seen = entry;
            return Task.FromResult(TweakResult.Ok("test.script"));
        });

        var entry = EntryFor(tweak.Id);
        entry.CapturedState["startType"] = "Manual";
        _journal.Record(entry);

        var result = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Success, result.Status);
        Assert.Equal("Manual", seen?.CapturedState["startType"]);
        Assert.Null(_journal.Find(tweak.Id));
    }

    [Fact]
    public async Task Revert_whose_script_throws_is_a_failure_and_keeps_the_entry()
    {
        var tweak = MakeTweak("test.throws",
            scriptRevert: (_, _, _) => throw new InvalidOperationException("sc.exe went away"));

        var entry = EntryFor(tweak.Id);
        entry.CapturedState["x"] = "y";
        _journal.Record(entry);

        var result = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.NotNull(_journal.Find(tweak.Id));
    }

    [Fact]
    public async Task Apply_then_revert_puts_back_the_original_value()
    {
        var spec = Spec("A", desired: 0);
        var tweak = MakeTweak("test.roundtrip", [spec]);
        _registry.Set(spec.Root, spec.SubKey, spec.ValueName, 1);

        var applied = await _engine.ApplyAsync([tweak], "test", createRestorePoint: false);
        Assert.Equal(1, applied.Applied);
        Assert.Equal(0, _registry.ReadValue(spec.Root, spec.SubKey, spec.ValueName));

        var reverted = await _engine.RevertAsync(tweak);

        Assert.Equal(ResultStatus.Success, reverted.Status);
        Assert.Equal(1, _registry.ReadValue(spec.Root, spec.SubKey, spec.ValueName));
        Assert.False(_engine.HasUndoData);
    }

    [Fact]
    public async Task Apply_then_revert_removes_a_value_that_did_not_exist_before()
    {
        var spec = Spec("Absent", desired: 0);
        var tweak = MakeTweak("test.absent", [spec]);

        await _engine.ApplyAsync([tweak], "test", createRestorePoint: false);
        Assert.Equal(0, _registry.ReadValue(spec.Root, spec.SubKey, spec.ValueName));

        await _engine.RevertAsync(tweak);

        Assert.Null(_registry.ReadValue(spec.Root, spec.SubKey, spec.ValueName));
    }

    [Fact]
    public async Task Apply_records_no_undo_data_when_the_value_was_already_set()
    {
        var spec = Spec("A", desired: 0);
        var tweak = MakeTweak("test.noop", [spec]);
        _registry.Set(spec.Root, spec.SubKey, spec.ValueName, 0);

        var summary = await _engine.ApplyAsync([tweak], "test", createRestorePoint: false);

        Assert.Equal(1, summary.AlreadyDone);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task Revert_all_undoes_in_reverse_order_and_skips_what_it_cannot_revert()
    {
        // Real tweaks, since revert-all looks them up by ID, but only registry-only ones so no script runs.
        var registryOnly = _engine.AllTweaks
            .Where(t => t.ScriptApply is null && t.ScriptRevert is null && t.RegistryValues.Count > 0)
            .Take(2)
            .ToList();
        Assert.Equal(2, registryOnly.Count);

        var first = registryOnly[0];
        var second = registryOnly[1];

        _journal.Record(EntryFor(first.Id, SnapshotOf(first.RegistryValues[0], original: 7)));
        _journal.Record(EntryFor("no.such.tweak", SnapshotOf(Spec("Unknown"), original: 1)));
        _journal.Record(EntryFor(second.Id, SnapshotOf(second.RegistryValues[0], original: 8)));
        _journal.Record(EntryFor(TweakJournalService.RemovalRecordPrefix + "Microsoft.BingNews"));

        var summary = await _engine.RevertAllAsync();

        Assert.Equal(2, summary.Applied);
        Assert.Equal(
            [second.RegistryValues[0].ValueName, first.RegistryValues[0].ValueName],
            _registry.Restored.Select(s => s.ValueName));

        // The unknown entry and the removal record are left alone.
        Assert.Equal(
            ["no.such.tweak", TweakJournalService.RemovalRecordPrefix + "Microsoft.BingNews"],
            _journal.Entries.Select(e => e.TweakId));
    }
}
