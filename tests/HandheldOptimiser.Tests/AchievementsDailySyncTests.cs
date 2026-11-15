using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class AchievementsDailySyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(13));

    [Fact]
    public void Due_when_never_synced() => Assert.True(Achievements.SyncAllDue(null, Now));

    [Fact]
    public void Not_due_within_a_day() => Assert.False(Achievements.SyncAllDue(Now.AddHours(-23), Now));

    [Fact]
    public void Due_after_a_day() => Assert.True(Achievements.SyncAllDue(Now.AddDays(-1), Now));

    [Fact]
    public void Due_when_the_last_sync_is_in_the_future() => Assert.True(Achievements.SyncAllDue(Now.AddHours(2), Now));
}
