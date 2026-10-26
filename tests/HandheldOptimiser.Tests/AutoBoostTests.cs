using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// CPU boost in Auto: off on battery, and plugged in, on only while the game needs it, with a gap between
/// changes so it does not flick on and off.
/// </summary>
public class AutoBoostTests
{
    private const int Top = 30;
    private static readonly DateTime Start = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => Start.AddSeconds(seconds);

    /// <summary>Plugged in at 60 fps short of the target for 5 seconds, from second 0: on at second 5.</summary>
    private static AutoBoost TurnedOn()
    {
        var boost = new AutoBoost(startsOn: false);
        for (var second = 0; second < 5; second++)
        {
            boost.Decide(At(second), onBattery: false, 55, 60, Top, Top);
        }

        Assert.True(boost.Decide(At(5), onBattery: false, 55, 60, Top, Top));
        return boost;
    }

    [Fact]
    public void On_battery_it_goes_off_at_once() =>
        Assert.False(new AutoBoost(startsOn: true).Decide(Start, onBattery: true, 60, 60, 20, 25));

    [Fact]
    public void On_battery_with_it_already_off_there_is_nothing_to_change() =>
        Assert.Null(new AutoBoost(startsOn: false).Decide(Start, onBattery: true, 60, 60, 20, 25));

    [Fact]
    public void A_session_starts_with_it_off_even_plugged_in() =>
        Assert.False(new AutoBoost(startsOn: true).Decide(Start, onBattery: false, 60, 60, 20, Top));

    [Fact]
    public void Plugged_in_short_of_the_target_for_5_seconds_turns_it_on()
    {
        var boost = new AutoBoost(startsOn: false);
        for (var second = 0; second <= 4; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 55, 60, Top, Top));
        }

        Assert.True(boost.Decide(At(5), onBattery: false, 55, 60, Top, Top));
    }

    [Fact]
    public void Two_fps_short_is_not_short_enough()
    {
        var boost = new AutoBoost(startsOn: false);
        for (var second = 0; second <= 30; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 58, 60, Top, Top));
        }
    }

    [Fact]
    public void With_no_target_5_seconds_at_the_top_turns_it_on()
    {
        var boost = new AutoBoost(startsOn: false);
        for (var second = 0; second <= 4; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 80, 0, Top - 1, Top));
        }

        Assert.True(boost.Decide(At(5), onBattery: false, 80, 0, Top - 1, Top));
    }

    [Fact]
    public void Thirty_seconds_well_under_the_top_with_the_target_held_turns_it_off()
    {
        var boost = TurnedOn();
        for (var second = 6; second <= 35; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 60, 60, 20, Top));
        }

        Assert.False(boost.Decide(At(36), onBattery: false, 60, 60, 20, Top));
    }

    [Fact]
    public void Under_the_top_but_short_of_the_target_keeps_it_on()
    {
        var boost = TurnedOn();
        for (var second = 6; second <= 90; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 58, 60, 20, Top));
        }
    }

    [Fact]
    public void No_change_within_20_seconds_of_the_last()
    {
        var boost = TurnedOn();
        for (var second = 6; second <= 35; second++)
        {
            boost.Decide(At(second), onBattery: false, 60, 60, 20, Top);
        }

        Assert.False(boost.Decide(At(36), onBattery: false, 60, 60, 20, Top));

        // Short again from second 37: 5 seconds would be enough, but not until 20 seconds after the last change.
        for (var second = 37; second <= 55; second++)
        {
            Assert.Null(boost.Decide(At(second), onBattery: false, 55, 60, Top, Top));
        }

        Assert.True(boost.Decide(At(56), onBattery: false, 55, 60, Top, Top));
    }

    [Fact]
    public void Unplugging_within_20_seconds_still_turns_it_off()
    {
        var boost = TurnedOn();
        Assert.False(boost.Decide(At(6), onBattery: true, 55, 60, 25, 25));
    }
}
