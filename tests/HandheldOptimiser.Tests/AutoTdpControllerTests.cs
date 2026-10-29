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

    // ----- Auto with no frame rate: the most frames for the least power -----

    private static readonly AutoTdpSettings NoTarget = new(AutoTdpProtocol.NoTargetFps, MinWatts: 7, MaxWatts: 30, TemperatureLimit: 80);

    [Fact]
    public void With_no_target_a_game_that_still_gains_from_power_keeps_the_top()
    {
        // Every watt still buys frames, if fewer each time: a quarter less power costs about 12% of them,
        // which a target's idle-menu allowance would accept, but with no target it is frames thrown away.
        var controller = new AutoTdpController(NoTarget, startWatts: 30);
        var watts = Run(controller, 400, (_, w) => 60 * Math.Sqrt(w / 30.0));

        var top = watts.TakeLast(120).Count(w => w >= 28);
        Assert.True(top >= 100, $"Only {top} of the last 120 seconds at 28 W or more");
    }

    [Fact]
    public void With_no_target_steps_down_from_the_top_do_not_add_up()
    {
        // Each quarter less power costs about 4% of the frames, under any one step's allowance, but three
        // in a row cost 12% against the top. Judged against the top, it stops before they add up.
        var controller = new AutoTdpController(NoTarget, startWatts: 30);
        var watts = Run(controller, 600, (_, w) => 60 * Math.Pow(w / 30.0, 0.15));

        Assert.True(watts.TakeLast(120).Min() >= 15, $"Went down to {watts.TakeLast(120).Min()} W");
    }

    [Fact]
    public void With_no_target_a_game_more_power_cannot_help_still_comes_down()
    {
        // Capped by the processor at 45 fps from 12 W up: power above that buys nothing.
        var controller = new AutoTdpController(NoTarget, startWatts: 30);
        var watts = Run(controller, 400, (_, w) => Math.Min(45, 4.0 * w));

        var low = watts.TakeLast(120).Count(w => w <= 18);
        Assert.True(low >= 100, $"Only {low} of the last 120 seconds at 18 W or under");
    }

    [Fact]
    public void A_game_idling_at_the_top_limit_is_stepped_down_after_a_minute()
    {
        // Like Fortnite left at its menu: 30 fps whatever the limit, from the top limit, short of 60 for good.
        var controller = new AutoTdpController(Sixty, startWatts: 30);
        var watts = Run(controller, 300, (_, w) => Math.Min(30, 4.0 * w));

        // Nothing changes in the first minute: a game short of its target at the top may still need it.
        Assert.All(watts.Take(55), w => Assert.Equal(30, w));

        // Then down near the 8 W that 30 fps needs, and kept there bar a short check once a minute.
        var low = watts.TakeLast(120).Count(w => w <= 10);
        Assert.True(low >= 100, $"Only {low} of the last 120 seconds at 10 W or under");
    }

    [Fact]
    public void A_noisy_idle_menu_is_stepped_down_too()
    {
        // As Fortnite's menu really was on the Ally: anywhere from 20 to 35 fps from one second to the next,
        // whatever the limit above the 9 W that needs. Eight runs, as one unlucky run may climb back up for
        // a minute or two before the next check brings it down again.
        var low = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var random = new Random(seed);
            var controller = new AutoTdpController(Sixty with { MaxWatts = 25 }, startWatts: 25);
            var watts = Run(controller, 420, (_, w) => Math.Min(random.Next(20, 36), 4.0 * w));

            Assert.True(watts.Take(300).Min() <= 9, $"Run {seed} never got under {watts.Take(300).Min()} W");
            low += watts.TakeLast(180).Count(w => w <= 12);
        }

        Assert.True(low >= 8 * 180 * 3 / 4, $"Only {low} of 1440 seconds at 12 W or under over the last three minutes");
    }

    [Fact]
    public void An_idle_menu_with_dips_is_stepped_down_and_stays_down()
    {
        // As Fortnite's 30 fps sleep menu was on the Ally on 2026-10-20: 20 to 39 fps from one second to the
        // next, with the odd second far lower, whatever the limit above the 10 W that needs. A second under
        // half its neighbours used to throw away the step being judged, and a cluster of them let a held
        // limit go, so the limit climbed back to the top: on 50 such menus it spent 44% of the time at 28 W or
        // over. Steps of a quarter reach 11 W, as 8 W costs frames. A long run of unlucky seconds can still
        // send it up for a minute, so this counts time rather than asking that no run ever climbs.
        int seconds = 0, low = 0, top = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var random = new Random(seed);
            var controller = new AutoTdpController(Sixty, startWatts: 30);
            var watts = Run(controller, 900, (_, w) => Math.Min(random.Next(100) < 12 ? random.Next(8, 15) : random.Next(20, 40), 4.0 * w));

            var down = watts.FindIndex(w => w <= 14);
            Assert.True(down is >= 0 and < 600, $"Run {seed} took {down} seconds to come down");

            var after = watts.Skip(down).ToList();
            seconds += after.Count;
            low += after.Count(w => w <= 14);
            top += after.Count(w => w >= 28);
        }

        Assert.True(low >= seconds * 4 / 5, $"Only {low} of {seconds} seconds at 14 W or under once down");
        Assert.True(top <= seconds / 20, $"{top} of {seconds} seconds at 28 W or over once down");
    }

    [Fact]
    public void A_game_that_needs_the_top_limit_keeps_it()
    {
        // 1.8 fps a watt: 54 fps at the top limit of 30 W, so every watt counts.
        var controller = new AutoTdpController(Sixty, startWatts: 30);
        var watts = Run(controller, 400, (_, w) => 1.8 * w);

        Assert.Equal(30, watts[^1]);

        // A step down costs frames, so it is taken back, and the next try waits longer each time.
        var below = watts.Count(w => w < 30);
        Assert.True(below <= 25, $"{below} seconds under the top limit");
    }

    [Fact]
    public void Going_back_into_a_match_lifts_the_cap_at_once()
    {
        // The menu as above for four minutes, then a match that needs 24 W for 60 fps.
        var controller = new AutoTdpController(Sixty, startWatts: 30);
        var watts = Run(controller, 280, (second, w) => second < 240 ? Math.Min(30, 4.0 * w) : 2.5 * w);

        Assert.True(watts[239] <= 12, $"Was at {watts[239]} W in the menu");
        Assert.True(watts[^1] >= 20, $"Only reached {watts[^1]} W 40 seconds into the match");
    }

    [Fact]
    public void A_lighter_scene_lifts_the_cap_too()
    {
        // The menu, then a light scene that would reach 60 fps from 10 W, more than the menu's 30 at the cap.
        // Most of 10 seconds have to move, for 8 seconds, so it is let go within half a minute.
        var controller = new AutoTdpController(Sixty, startWatts: 30);
        var watts = Run(controller, 270, (second, w) => second < 240 ? Math.Min(30, 4.0 * w) : 6.0 * w);

        Assert.True(watts[239] <= 9, $"Was at {watts[239]} W in the menu");
        Assert.True(watts[^1] >= 10, $"Stayed at {watts[^1]} W, short of 60 fps");
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
    public void Just_under_the_temperature_limit_it_holds_rather_than_cutting_on()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 25);

        // Over the 80 °C limit for 10 seconds, then 79 °C: under the limit, but not yet the 3 degrees under
        // it that counts as cooled. The cuts so far are holding the temperature, so nothing more is cut.
        var watts = Run(controller, 70, (_, w) => 2.0 * w, (second, _) => second < 10 ? 85 : 79);

        Assert.True(watts[9] < 25, $"Stayed at {watts[9]} W while hot");
        Assert.All(watts.Skip(10), w => Assert.Equal(watts[9], w));
        Assert.True(watts[^1] > Sixty.MinWatts, "Cut down to the minimum");
        Assert.Equal(AutoTdpState.TooHot, controller.State);
    }

    [Fact]
    public void Staying_over_the_temperature_limit_keeps_cutting()
    {
        var controller = new AutoTdpController(Sixty, startWatts: 25);
        var watts = Run(controller, 120, (_, w) => 2.0 * w, (_, _) => 85);

        Assert.Equal(Sixty.MinWatts, watts[^1]);
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
    public void Short_bursts_of_spare_frames_do_not_step_it_down()
    {
        // Like Fortnite at 60 fps on the Ally on 2026-10-21: a little short of the target even at the top
        // limit (1.9 fps a watt, 57 fps at 30 W, give or take 3), with a few seconds now and then well over
        // it, as when the player looks at the sky. Those seconds were stepped down on, and raised straight
        // back: 56 times in two hours. Eight runs of ten minutes.
        var lowered = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var random = new Random(seed);
            var controller = new AutoTdpController(Sixty, startWatts: 30);
            var watts = Run(controller, 600, (second, w) => 1.9 * w + random.Next(-3, 4) + (second % 40 < 4 ? 25 : 0));

            // A step down that is not one of the checks from the top, which take a quarter (7 W) at a time.
            lowered += watts.Zip(watts.Skip(1)).Count(pair => pair.Second < pair.First && pair.First - pair.Second < 7);
        }

        Assert.True(lowered <= 2, $"{lowered} small steps down in 80 minutes");
    }

    [Fact]
    public void Reaching_the_temperature_limit_again_and_again_keeps_the_limit_under_it()
    {
        // A game that wants all 30 W, on a device that reaches 80 °C from 28 W and is cool under it. Without a
        // memory the limit climbed back a watt every 20 seconds and reached the heat every minute or so, as
        // it did 15 times in two hours of Fortnite.
        var controller = new AutoTdpController(Sixty, startWatts: 26);
        var watts = Run(controller, 1200, (_, w) => 2.0 * w, (_, w) => w >= 28 ? 86 : 74);

        var hits = watts.Zip(watts.Skip(1)).Count(pair => pair.First < 28 && pair.Second >= 28);
        Assert.True(hits <= 4, $"Reached the heat {hits} times in 20 minutes");

        // Still using what it can: a watt under the heat, not cut to the bottom. Under there only for the
        // checks from the top, a quarter down for a few seconds now and then.
        var under = watts.TakeLast(600).Count(w => w < 26);
        Assert.True(under <= 60, $"{under} of the last 600 seconds under 26 W");
    }

    [Fact]
    public void Heat_that_stops_lets_the_limit_climb_again_after_ten_minutes()
    {
        // Hot from 28 W for the first five minutes, then cool at any limit, as when the player moves somewhere
        // cooler or the fans catch up. Once ten minutes pass with no heat, the remembered limit goes.
        var controller = new AutoTdpController(Sixty, startWatts: 26);
        var watts = Run(controller, 1500, (_, w) => 2.0 * w, (second, w) => second < 300 && w >= 28 ? 86 : 74);

        Assert.Equal(Sixty.MaxWatts, watts[^1]);
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
