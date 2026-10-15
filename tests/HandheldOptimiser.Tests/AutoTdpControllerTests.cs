using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The TDP Engine controller against simple pretend games: each second the game's frame rate comes from
/// the power limit the controller last set.
/// </summary>
public class AutoTdpControllerTests
{
    private static readonly AutoTdpSettings Sixty = new(TargetFps: 60, MinWatts: 7, MaxWatts: 30, TemperatureLimit: 80);

    private static readonly DateTime Start = new(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Runs the controller for a number of seconds, returning the power limit after each.</summary>
    private static List<int> Run(AutoTdpController controller, int seconds, Func<int, int, double> fps,
        Func<int, int, double?>? temperature = null)
    {
        var watts = new List<int>();
        for (var second = 0; second < seconds; second++)
        {
            var now = Start.AddSeconds(second);
            controller.Tick(now, fps(second, controller.Watts), temperature?.Invoke(second, controller.Watts));
            watts.Add(controller.Watts);
        }

        return watts;
    }

    private static int Changes(IEnumerable<int> watts) => watts.Zip(watts.Skip(1)).Count(pair => pair.First != pair.Second);

    [Fact]
    public void An_uncapped_game_comes_down_to_the_least_power_that_holds_the_target()
    {
        // 4 fps a watt: 60 fps needs 15 W.
        var controller = new AutoTdpController(Sixty, startWatts: 25);
        var watts = Run(controller, 300, (_, w) => 4.0 * w);

        Assert.InRange(watts[^1], 15, 16);

        // Settled: no more than a probe now and then over the last two minutes.
        Assert.True(Changes(watts.TakeLast(120)) <= 6, $"{Changes(watts.TakeLast(120))} changes");
    }

    [Fact]
    public void Noisy_frame_rates_do_not_make_it_flap()
    {
        // As above, with each second's count up to 3 frames either way.
        var random = new Random(7);
        var controller = new AutoTdpController(Sixty, startWatts: 25);
        var watts = Run(controller, 600, (_, w) => 4.0 * w + random.Next(-3, 4));

        Assert.InRange(watts.TakeLast(120).Min(), 14, 17);
        Assert.True(Changes(watts.TakeLast(300)) <= 20, $"{Changes(watts.TakeLast(300))} changes in five minutes");
    }

    [Fact]
    public void A_heavy_scene_raises_the_limit_quickly()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 10);

        // 3 fps a watt: 60 fps needs 20 W, and 10 W gives 30 fps, half the target.
        var watts = Run(controller, 40, (_, w) => 3.0 * w);

        Assert.True(watts[^1] >= 20, $"Only reached {watts[^1]} W");
    }

    [Fact]
    public void A_game_capped_at_the_target_does_not_saw_at_its_edge()
    {
        // Capped at 60, which it reaches at 12 W.
        var controller = new AutoTdpController(Sixty, startWatts: 25);
        var watts = Run(controller, 600, (_, w) => Math.Min(60, 5.0 * w));

        Assert.InRange(watts[^1], 11, 13);

        // Each probe below the edge costs a dip, so they come further apart: few over the last five minutes.
        var probes = watts.TakeLast(300).Count(w => w < 12);
        Assert.True(probes <= 20, $"{probes} seconds spent below the edge");
    }

    [Fact]
    public void A_game_that_more_power_cannot_help_is_not_given_it()
    {
        // Held back by its processor at 45 fps from 12 W up: the target of 60 is out of reach.
        var controller = new AutoTdpController(Sixty, startWatts: 10);
        var watts = Run(controller, 120, (_, w) => Math.Min(45, 4.0 * w));

        Assert.True(watts.TakeLast(30).Max() <= 16, $"Went to {watts.TakeLast(30).Max()} W");
    }

    [Fact]
    public void Heat_lowers_the_limit_whatever_the_frame_rate()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 25);

        // Short of the target at any limit, and 85 °C, over the 80 °C limit.
        var watts = Run(controller, 30, (_, w) => 2.0 * w, (_, _) => 85);

        Assert.True(watts[^1] < 25, $"Stayed at {watts[^1]} W");
        Assert.Equal(AutoTdpState.TooHot, controller.State);
    }

    [Fact]
    public void After_cooling_the_limit_climbs_back_slowly()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 20);

        // Hot for the first 20 seconds, cool after; the game wants more power throughout.
        var watts = Run(controller, 80, (_, w) => 2.0 * w, (second, _) => second < 20 ? 85 : 70);
        var lowest = watts.Take(25).Min();

        // Back up by 3 W or less in the minute after: a watt each 20 seconds.
        Assert.True(watts[^1] - lowest <= 3, $"From {lowest} W to {watts[^1]} W");
    }

    [Fact]
    public void Hitches_and_spikes_do_not_push_the_limit_up()
    {
        // Like Overcooked 2 on the Ally: a light game that holds 60 from 12 W, but some seconds drop to a
        // handful of frames (level changes, stutters) and others jump to hundreds (uncapped menus).
        var random = new Random(11);
        var controller = new AutoTdpController(Sixty, startWatts: 19);
        var watts = Run(controller, 600, (_, w) => random.Next(100) switch
        {
            < 15 => random.Next(9, 16),
            < 25 => random.Next(170, 303),
            _ => Math.Min(60, 5.0 * w) + random.Next(-1, 2)
        });

        Assert.True(watts.TakeLast(300).Max() <= 15, $"Went to {watts.TakeLast(300).Max()} W");
        Assert.InRange(watts[^1], 11, 15);
    }

    [Fact]
    public void Loading_screens_change_nothing()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 15);
        var watts = Run(controller, 60, (_, _) => 0);

        Assert.All(watts, w => Assert.Equal(15, w));
        Assert.Equal(AutoTdpState.Waiting, controller.State);
    }

    [Fact]
    public void It_stays_inside_the_limits_it_is_given()
    {
        var settings = Sixty with { MinWatts = 8, MaxWatts = 20 };
        var high = Run(new AutoTdpController(settings, startWatts: 15), 120, (_, w) => 1.0 * w);
        var low = Run(new AutoTdpController(settings, startWatts: 15), 120, (_, _) => 200);

        Assert.Equal(20, high[^1]);
        Assert.Equal(8, low[^1]);
        Assert.All(high.Concat(low), w => Assert.InRange(w, 8, 20));
    }
}
