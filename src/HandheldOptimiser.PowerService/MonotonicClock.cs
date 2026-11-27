using System.Diagnostics;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// A monotonic clock backed by <see cref="Stopwatch"/> expressed as a <see cref="DateTime"/> offset from
/// a fixed epoch, so NTP syncs, DST changes, or manual clock edits never make intervals jump backward or
/// forward while keeping <see cref="DateTime"/> timestamps compatible with pure unit tests.
/// </summary>
internal static class MonotonicClock
{
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static DateTime UtcNow => Epoch + Stopwatch.GetElapsedTime(Origin);
}
