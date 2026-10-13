using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>How many of a set of tweaks are applied, and how many have been checked at all.</summary>
public readonly record struct TweakSummary(int Applied, int Answered, int Total)
{
    /// <summary>False while any of them is still waiting for its first answer.</summary>
    public bool IsComplete => Answered == Total;
}

/// <summary>
/// The state of every tweak, checked once and read by every page.
///
/// Each page used to check its own tweaks when it was opened, and again after anything changed anywhere,
/// holding the whole app busy while it did. Now everything is checked once at start-up and again after
/// a change, in the background, and pages show what is here.
///
/// Checks only read, so several can be out for the same tweak at once: the scan at start-up, a
/// Re-check, the check after an apply. The answer to the newest question is the one kept, whatever
/// order they come back in, so a slow check from before a change can never overwrite what was found
/// after it. Used from the UI thread only.
/// </summary>
public sealed class TweakStateStore
{
    private sealed class Entry(Tweak tweak)
    {
        public Tweak Tweak { get; } = tweak;
        public TweakState? State { get; set; }

        /// <summary>How many times this tweak has been asked about, and which of those was last answered.</summary>
        public int Asked { get; set; }
        public int Answered { get; set; }

        /// <summary>The newest question, for anyone who needs to wait on it.</summary>
        public Task Newest { get; set; } = Task.CompletedTask;
    }

    private readonly Func<Tweak, CancellationToken, Task<TweakState>> _detect;
    private readonly Action<Tweak, Exception>? _onError;
    private readonly Dictionary<string, Entry> _entries;

    public TweakStateStore(
        IReadOnlyList<Tweak> tweaks,
        Func<Tweak, CancellationToken, Task<TweakState>> detect,
        Action<Tweak, Exception>? onError = null)
    {
        _detect = detect;
        _onError = onError;
        _entries = tweaks.ToDictionary(t => t.Id, t => new Entry(t), StringComparer.Ordinal);
        Tweaks = tweaks;
    }

    /// <summary>Every tweak this holds a state for.</summary>
    public IReadOnlyList<Tweak> Tweaks { get; }

    /// <summary>Raised when a tweak starts being checked and when its newest check answers.</summary>
    public event Action<Tweak>? Changed;

    /// <summary>The last state found, or null if the tweak has never been checked.</summary>
    public TweakState? StateOf(Tweak tweak) => _entries.TryGetValue(tweak.Id, out var entry) ? entry.State : null;

    /// <summary>Whether the tweak's newest check is still out.</summary>
    public bool IsChecking(Tweak tweak) =>
        _entries.TryGetValue(tweak.Id, out var entry) && entry.Asked != entry.Answered;

    public TweakSummary Summarise(IEnumerable<Tweak> tweaks)
    {
        var states = tweaks.Select(StateOf).ToList();
        return new TweakSummary(
            states.Count(s => s == TweakState.Applied),
            states.Count(s => s is not null),
            states.Count);
    }

    /// <summary>Checks the given tweaks again. Finishes when each has answered its newest question.</summary>
    public Task CheckAsync(IEnumerable<Tweak> tweaks, CancellationToken ct = default) =>
        Task.WhenAll(tweaks.Select(t => Ask(t, ct)).ToList());

    /// <summary>
    /// Checks every tweak again, and finishes when the ones named have answered, or all of them when
    /// none is named. After a change this lets the page wait for the tweak it changed and no longer.
    /// </summary>
    public Task CheckAllAsync(IEnumerable<Tweak>? waitFor = null, CancellationToken ct = default)
    {
        var all = _entries.Values.Select(e => (e.Tweak.Id, Check: Ask(e.Tweak, ct))).ToList();

        if (waitFor is null)
        {
            return Task.WhenAll(all.Select(a => a.Check));
        }

        var wanted = waitFor.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return Task.WhenAll(all.Where(a => wanted.Contains(a.Id)).Select(a => a.Check));
    }

    /// <summary>
    /// Checks the tweaks that have never been asked about. A page calls this when it is opened, so it
    /// still fills in if the scan at start-up did not run.
    /// </summary>
    public Task CheckMissingAsync(IEnumerable<Tweak> tweaks, CancellationToken ct = default) =>
        CheckAsync(tweaks.Where(t => _entries.TryGetValue(t.Id, out var entry) && entry.Asked == 0).ToList(), ct);

    private Task Ask(Tweak tweak, CancellationToken ct)
    {
        if (!_entries.TryGetValue(tweak.Id, out var entry))
        {
            return Task.CompletedTask;
        }

        var question = ++entry.Asked;
        Changed?.Invoke(entry.Tweak);

        // The newest question is what a caller waits on. An older one answering first does not finish
        // the wait, since its answer is thrown away.
        return entry.Newest = AnswerAsync(entry, question, ct);
    }

    private async Task AnswerAsync(Entry entry, int question, CancellationToken ct)
    {
        TweakState? state;

        try
        {
            state = await _detect(entry.Tweak, ct);
        }
        catch (OperationCanceledException)
        {
            // Not an answer: what was known before still stands.
            state = entry.State;
        }
        catch (Exception ex)
        {
            _onError?.Invoke(entry.Tweak, ex);
            state = TweakState.Unknown;
        }

        if (question != entry.Asked)
        {
            // A newer question went out while this one was being answered. Whoever waited on this one
            // waits on that one now.
            await entry.Newest;
            return;
        }

        entry.State = state;
        entry.Answered = question;
        Changed?.Invoke(entry.Tweak);
    }
}
