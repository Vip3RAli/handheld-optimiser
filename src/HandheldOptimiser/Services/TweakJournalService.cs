using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using HandheldOptimiser.Models;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.Services;

/// <summary>
/// Persists what each tweak changed so it can be undone on a later run of the app, not just in the
/// session that applied it. System Restore is the blunt instrument; this is the scalpel.
///
/// Kept in HKLM rather than a file under %LOCALAPPDATA%. This app runs elevated and puts the journal's
/// contents back into the registry and into scripts, so wherever it lives must be writable by
/// administrators only. HKLM\SOFTWARE is: standard users and unelevated processes can read it but not
/// create or change anything there. A per-user file was not, and anything running as the user could
/// have planted an entry that "Revert" would then write with administrator rights.
///
/// One value per user SID, since the journal holds HKCU snapshots that only mean something for the
/// user who applied them. A registry value is also replaced in one step, so a crash mid-save leaves the
/// previous journal intact.
/// </summary>
public sealed class TweakJournalService(LogService log) : ITweakJournal
{
    private const string JournalKey = @"SOFTWARE\HandheldOptimiser\UndoJournal";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LogService _log = log;
    private readonly string _valueName = WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

    // Where versions up to 0.2.0 kept the journal. Only ever read once, to import it.
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "journal.json");

    private TweakJournal? _cached;
    private bool _foundInRegistry;
    private bool _saveBlocked;

    public string Location => $@"HKLM\{JournalKey}\{_valueName}";

    /// <summary>
    /// Bloatware removals are journalled under this prefix as a record of what went, so it can be
    /// reinstalled. They are not undo data: no tweak has these IDs and nothing can revert them.
    /// </summary>
    public const string RemovalRecordPrefix = "appx.removal.";

    /// <summary>
    /// Undo data keys whose value is a list, one item per line, rather than a single original value.
    /// A re-apply adds the items it changed to the list instead of being ignored, or revert would only
    /// put back what the first run changed.
    /// </summary>
    private static readonly HashSet<string> ListStateKeys = new(StringComparer.Ordinal)
    {
        SleepTweaks.WakeDevices,
        SleepTweaks.CardReaders
    };

    /// <summary>
    /// Undo data keys that are not imported from the old per-user file. Their values are device names
    /// that revert pastes into an elevated script and cannot check against a fixed list, so one
    /// planted there could make revert act on whatever it names. Every other key's value is checked
    /// at revert time against what its tweak could have written (a GUID, a number, a known name).
    /// </summary>
    private static readonly string[] UntrustedLegacyStateKeys =
    [
        SleepTweaks.WakeDevices,
        SleepTweaks.CardReaders
    ];

    public TweakJournal Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string? json = null;

        try
        {
            using var baseKey = OpenBase();
            using var key = baseKey.OpenSubKey(JournalKey, writable: false);
            json = key?.GetValue(_valueName) as string;
            _foundInRegistry = json is not null;
            _cached = json is null
                ? new TweakJournal()
                : JsonSerializer.Deserialize<TweakJournal>(json, JsonOptions) ?? new TweakJournal();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warning($"Could not read the undo journal ({ex.Message}).");
            _cached = new TweakJournal();
            PreserveUnreadableJournal(json);
        }

        return _cached;
    }

    /// <summary>
    /// The next save would overwrite the unreadable value, and it may hold the only undo data for tweaks
    /// applied in earlier sessions. Copy it aside first; if even that fails, refuse to save at all.
    /// </summary>
    private void PreserveUnreadableJournal(string? json)
    {
        if (json is null)
        {
            // Could not even be read, so there is nothing to copy; overwriting it would lose it.
            _saveBlocked = true;
            _log.Error("Undo data will not be saved this session so the existing journal is not overwritten.");
            return;
        }

        var backupName = $"{_valueName}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";

        try
        {
            using var baseKey = OpenBase();
            using var key = baseKey.CreateSubKey(JournalKey, writable: true);
            key.SetValue(backupName, json, RegistryValueKind.String);
            _log.Warning($@"Kept a copy as HKLM\{JournalKey}\{backupName}. Starting a fresh journal; tweaks " +
                         "applied before this session will not be individually revertable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _saveBlocked = true;
            _log.Error($"Could not back up the undo journal ({ex.Message}). Undo data will not be saved " +
                       "this session so the existing journal is not overwritten.");
        }
    }

    /// <summary>
    /// Moves undo data from the old per-user file into the registry, once. Called at startup, before
    /// anything reads the journal for real.
    ///
    /// That file could have been edited by anything running as the user, so it is not taken on trust:
    /// entries for IDs this build does not know are dropped, and every registry snapshot is checked
    /// again at revert time against the values its tweak actually owns (<see cref="Tweak.OwnsRegistryValue"/>).
    /// Captured state that revert cannot check the same way, device names, is left behind
    /// (<see cref="DropUntrustedLegacyState"/>).
    /// </summary>
    public void ImportLegacyFile(Func<string, bool> isKnownTweakId)
    {
        var journal = Load();

        if (_foundInRegistry || _saveBlocked || !File.Exists(_legacyPath))
        {
            return;
        }

        _log.Info($"Moving undo data from {_legacyPath} to {Location}.");

        TweakJournal? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<TweakJournal>(File.ReadAllText(_legacyPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Left where it is for the user to look at. The empty journal saved below stops this being
            // retried, and warned about, on every start.
            _log.Warning($"Could not read the old undo journal ({ex.Message}); it was left in place and not imported.");
            Save();
            return;
        }

        foreach (var entry in legacy?.Entries ?? [])
        {
            var isRemovalRecord = entry.TweakId.StartsWith(RemovalRecordPrefix, StringComparison.Ordinal);

            if (!isRemovalRecord && !isKnownTweakId(entry.TweakId))
            {
                _log.Warning($"Not importing undo data for unknown tweak \"{entry.TweakId}\".");
                continue;
            }

            if (isRemovalRecord)
            {
                // A list of removed apps, nothing more; it never had registry data of its own.
                entry.RegistrySnapshots.Clear();
            }

            foreach (var key in DropUntrustedLegacyState(entry))
            {
                _log.Warning($"Not importing \"{key}\" for \"{entry.TweakId}\": it names devices that cannot be " +
                             "checked. Reverting this item will not put those devices back.");
            }

            if (journal.Entries.All(e => e.TweakId != entry.TweakId))
            {
                journal.Entries.Add(entry);
            }
        }

        if (!Save())
        {
            return;
        }

        _log.Success($"Imported undo data for {journal.Entries.Count} item(s).");

        // Deleted as administrator in a folder the user controls, so not through a link, or it could
        // be made to delete a journal.json somewhere else. The check here catches a link that is
        // already there; deleting through a handle whose real path is checked closes the gap between
        // that check and the delete.
        if (SafetyGuard.FindLinkOnPath(_legacyPath) is { } link)
        {
            _log.Warning($"Left the old journal file in place: {link} is a junction or symbolic link.");
            return;
        }

        if (!LinkSafeDelete.TryDelete(_legacyPath, out var problem))
        {
            // Harmless: once the registry value exists the file is never read again.
            _log.Trace($"    Could not delete the old journal file: {problem}");
        }
    }

    /// <summary>
    /// Removes the captured state an imported entry may not bring with it, and returns the keys removed.
    /// Public so the rule can be tested without the registry.
    /// </summary>
    public static IReadOnlyList<string> DropUntrustedLegacyState(TweakJournalEntry entry)
    {
        var dropped = new List<string>();

        foreach (var key in UntrustedLegacyStateKeys)
        {
            if (entry.CapturedState.Remove(key))
            {
                dropped.Add(key);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Adds a re-apply's captured state to what is already journalled and returns how many items were
    /// added. A single value keeps the first one captured, which is the original; a list gains the
    /// items it did not have yet. Public so it can be tested without the registry.
    /// </summary>
    public static int MergeCapturedState(Dictionary<string, string> existing, IReadOnlyDictionary<string, string> incoming)
    {
        var added = 0;

        foreach (var (key, value) in incoming)
        {
            if (existing.TryAdd(key, value))
            {
                added++;
                continue;
            }

            if (!ListStateKeys.Contains(key))
            {
                continue;
            }

            var items = SplitList(existing[key]);
            var newItems = SplitList(value)
                .Where(item => !items.Contains(item, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (newItems.Count > 0)
            {
                existing[key] = string.Join(SleepTweaks.DeviceSeparator, items.Concat(newItems));
                added += newItems.Count;
            }
        }

        return added;
    }

    private static List<string> SplitList(string value) =>
        [.. value.Split(SleepTweaks.DeviceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public void Record(TweakJournalEntry entry)
    {
        var journal = Load();
        var existing = journal.Entries.FirstOrDefault(e => e.TweakId == entry.TweakId);

        if (existing is null)
        {
            journal.Entries.Add(entry);
            Save();
            return;
        }

        // Re-applying a tweak must not overwrite the original pre-tweak snapshot with our own values,
        // or revert would restore the optimised state instead of the factory one. But a re-apply can
        // also change something the first run did not (a value Windows reset since, or one that was
        // already set back then), and its original belongs in the journal too. So only what is not
        // yet recorded is added.
        var added = 0;

        foreach (var snapshot in entry.RegistrySnapshots)
        {
            var known = existing.RegistrySnapshots.Any(s =>
                s.Root == snapshot.Root &&
                string.Equals(s.SubKey, snapshot.SubKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ValueName, snapshot.ValueName, StringComparison.OrdinalIgnoreCase));

            if (!known)
            {
                existing.RegistrySnapshots.Add(snapshot);
                added++;
            }
        }

        added += MergeCapturedState(existing.CapturedState, entry.CapturedState);

        if (added == 0)
        {
            _log.Trace($"Undo data for \"{entry.TweakName}\" already recorded; keeping the original snapshot.");
            return;
        }

        _log.Trace($"Added {added} newly changed item(s) to the undo data for \"{entry.TweakName}\".");
        Save();
    }

    public TweakJournalEntry? Find(string tweakId) =>
        Load().Entries.FirstOrDefault(e => e.TweakId == tweakId);

    /// <summary>Entries a tweak can be reverted from, excluding bloatware removal records.</summary>
    public IEnumerable<TweakJournalEntry> Revertable() =>
        Load().Entries.Where(e => !e.TweakId.StartsWith(RemovalRecordPrefix, StringComparison.Ordinal));

    public void Remove(string tweakId)
    {
        var journal = Load();
        var removed = journal.Entries.RemoveAll(e => e.TweakId == tweakId);
        if (removed > 0)
        {
            Save();
        }
    }

    private bool Save()
    {
        var journal = Load();

        if (_saveBlocked)
        {
            _log.Error("Undo data was not saved: the existing journal could not be read or backed up.");
            return false;
        }

        try
        {
            using var baseKey = OpenBase();
            using var key = baseKey.CreateSubKey(JournalKey, writable: true);
            key.SetValue(_valueName, JsonSerializer.Serialize(journal, JsonOptions), RegistryValueKind.String);
            _foundInRegistry = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error($"Could not save the undo journal: {ex.Message}");
            return false;
        }
    }

    private static RegistryKey OpenBase() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
}
