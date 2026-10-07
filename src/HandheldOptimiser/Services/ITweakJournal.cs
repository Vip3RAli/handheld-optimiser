using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// The undo journal as <see cref="TweakEngine"/> sees it. <see cref="TweakJournalService"/> is the only
/// real implementation; the interface exists so revert can be tested without touching HKLM.
/// </summary>
public interface ITweakJournal
{
    void Record(TweakJournalEntry entry);
    TweakJournalEntry? Find(string tweakId);
    IEnumerable<TweakJournalEntry> Revertable();
    void Remove(string tweakId);
}
