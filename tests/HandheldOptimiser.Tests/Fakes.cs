using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

// Every test here shares one LogService (it writes a session log file and is not built for concurrent
// use), and the engine tests share it with the guard tests, so nothing runs in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace HandheldOptimiser.Tests;

/// <summary>
/// An in-memory registry, so apply and revert can be exercised without touching the machine's own.
/// </summary>
internal sealed class FakeRegistry : IRegistryStore
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public List<RegistryValueSnapshot> Restored { get; } = [];

    /// <summary>When set, every restore reports failure, as a locked or missing key would.</summary>
    public bool FailRestores { get; set; }

    private static string Key(RegistryRoot root, string subKey, string valueName) => $@"{root}\{subKey}\{valueName}";

    public void Set(RegistryRoot root, string subKey, string valueName, object value) =>
        _values[Key(root, subKey, valueName)] = value;

    public object? ReadValue(RegistryRoot root, string subKey, string valueName) =>
        _values.TryGetValue(Key(root, subKey, valueName), out var value) ? value : null;

    public bool ValueMatches(RegistryValueSpec spec) =>
        Equals(ReadValue(spec.Root, spec.SubKey, spec.ValueName), spec.DesiredValue);

    public RegistryValueSnapshot? WriteValue(RegistryValueSpec spec)
    {
        var existing = ReadValue(spec.Root, spec.SubKey, spec.ValueName);
        var snapshot = new RegistryValueSnapshot
        {
            Root = spec.Root,
            SubKey = spec.SubKey,
            ValueName = spec.ValueName,
            KeyExisted = existing is not null,
            ValueExisted = existing is not null,
            OriginalValue = existing?.ToString(),
            OriginalKind = existing is null ? Microsoft.Win32.RegistryValueKind.Unknown : spec.Kind
        };

        Set(spec.Root, spec.SubKey, spec.ValueName, spec.DesiredValue);
        return snapshot;
    }

    public bool RestoreSnapshot(RegistryValueSnapshot snapshot)
    {
        Restored.Add(snapshot);

        if (FailRestores)
        {
            return false;
        }

        var key = Key(snapshot.Root, snapshot.SubKey, snapshot.ValueName);

        if (!snapshot.ValueExisted)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = snapshot.OriginalKind == Microsoft.Win32.RegistryValueKind.DWord
                ? int.Parse(snapshot.OriginalValue!)
                : snapshot.OriginalValue!;
        }

        return true;
    }
}

/// <summary>An in-memory undo journal, standing in for the one kept in HKLM.</summary>
internal sealed class FakeJournal : ITweakJournal
{
    public List<TweakJournalEntry> Entries { get; } = [];

    public void Record(TweakJournalEntry entry)
    {
        if (Entries.All(e => e.TweakId != entry.TweakId))
        {
            Entries.Add(entry);
        }
    }

    public TweakJournalEntry? Find(string tweakId) => Entries.FirstOrDefault(e => e.TweakId == tweakId);

    public IEnumerable<TweakJournalEntry> Revertable() =>
        Entries.Where(e => !e.TweakId.StartsWith(TweakJournalService.RemovalRecordPrefix, StringComparison.Ordinal));

    public void Remove(string tweakId) => Entries.RemoveAll(e => e.TweakId == tweakId);
}

internal static class TestLog
{
    public static LogService Instance { get; } = new();
}
