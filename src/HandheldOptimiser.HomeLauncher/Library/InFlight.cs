using System.Collections.Concurrent;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Requests on their way, by key, so asking again for the same thing joins the one already going rather
/// than starting another. The library asks for a game's artwork each time the focus lands on it, and
/// moving back and forth along the tiles would otherwise download the same picture several times over.
/// </summary>
/// <remarks>
/// ConcurrentDictionary.GetOrAdd can run its factory twice when two threads ask at once, and keeps only
/// one result; with the task itself as the value, both would have started a request. The value is a Lazy
/// instead, so only the stored one is ever started. It is removed only while it is still the stored one,
/// so a request that finishes late cannot take away the entry of a newer one for the same key.
/// </remarks>
internal sealed class InFlight<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _running = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> RunAsync(string key, Func<Task<T>> start)
    {
        var entry = _running.GetOrAdd(key, _ => new Lazy<Task<T>>(start, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await entry.Value;
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, entry));
        }
    }
}
