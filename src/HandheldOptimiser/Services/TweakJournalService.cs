using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Persists what each tweak changed so it can be undone on a later run of the app, not just in the
/// session that applied it. System Restore is the blunt instrument; this is the scalpel.
/// </summary>
public sealed class TweakJournalService(LogService log)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LogService _log = log;
    private readonly string _journalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "journal.json");

    private TweakJournal? _cached;

    public string JournalPath => _journalPath;

    public TweakJournal Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            if (File.Exists(_journalPath))
            {
                var json = File.ReadAllText(_journalPath);
                _cached = JsonSerializer.Deserialize<TweakJournal>(json, JsonOptions) ?? new TweakJournal();
            }
            else
            {
                _cached = new TweakJournal();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.Warning($"Could not read the undo journal ({ex.Message}). Starting a fresh one; " +
                         "previously applied tweaks will not be individually revertable.");
            _cached = new TweakJournal();
        }

        return _cached;
    }

    public void Record(TweakJournalEntry entry)
    {
        var journal = Load();

        // Re-applying a tweak must not overwrite the original pre-tweak snapshot with our own values,
        // or revert would restore the optimised state instead of the factory one.
        if (journal.Entries.Any(e => e.TweakId == entry.TweakId))
        {
            _log.Trace($"Undo data for \"{entry.TweakName}\" already recorded; keeping the original snapshot.");
            return;
        }

        journal.Entries.Add(entry);
        Save();
    }

    public TweakJournalEntry? Find(string tweakId) =>
        Load().Entries.FirstOrDefault(e => e.TweakId == tweakId);

    public void Remove(string tweakId)
    {
        var journal = Load();
        var removed = journal.Entries.RemoveAll(e => e.TweakId == tweakId);
        if (removed > 0)
        {
            Save();
        }
    }

    public IReadOnlyList<TweakJournalEntry> All() => Load().Entries;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(Load(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not save the undo journal: {ex.Message}");
        }
    }
}
