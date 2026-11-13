using System.Windows;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Where the Console space background's circles appear, how big they are and what kinds there are.</summary>
public class ConsoleCirclesTests
{
    [Fact]
    public void Every_circle_is_on_the_screen_and_within_its_sizes()
    {
        var random = new Random(3);
        var places = CircleScatter.Places(random, 1920, 1080);
        for (var i = 0; i < 2000; i++)
        {
            var spot = CircleScatter.Pick(random, 1920, 1080, places);
            Assert.InRange(spot.Centre.X, 0, 1920);
            Assert.InRange(spot.Centre.Y, 0, 1080);
            Assert.InRange(spot.Radius, CircleScatter.Smallest, CircleScatter.Largest);
            Assert.InRange(spot.Peak, 0.05, 0.4);
        }
    }

    [Fact]
    public void Most_circles_are_small_and_a_few_are_large()
    {
        var random = new Random(5);
        var radii = Enumerable.Range(0, 2000).Select(_ => CircleScatter.Pick(random, 1920, 1080, []).Radius).ToList();
        Assert.True(radii.Count(r => r < 30) > radii.Count / 2);
        Assert.Contains(radii, r => r > 90);
    }

    [Fact]
    public void Circles_are_sized_to_the_screen()
    {
        var spot = CircleScatter.Pick(new Random(9), 1280, 540, []);
        Assert.InRange(spot.Radius, CircleScatter.Smallest / 2, CircleScatter.Largest / 2);
    }

    [Fact]
    public void Most_circles_gather_round_the_places()
    {
        var random = new Random(11);
        Point[] places = [new(400, 300)];
        var near = Enumerable.Range(0, 1000)
            .Select(_ => CircleScatter.Pick(random, 1920, 1080, places).Centre)
            .Count(c => (c - places[0]).Length < 400);
        Assert.InRange(near, 550, 1000);
    }

    [Fact]
    public void Circles_keep_out_of_the_areas_kept_clear()
    {
        var random = new Random(13);
        Rect[] clear = [new(0, 0, 0.32, 0.36), new(0.8, 0, 0.2, 0.08)];
        Point[] places = [new(300, 200)];
        var spots = Enumerable.Range(0, 1000).Select(_ => CircleScatter.Pick(random, 1920, 1080, places, clear)).ToList();

        // A circle is let be after a dozen tries, so a very few may still land there; with a place right inside one, few do.
        Assert.True(spots.Count(s => CircleScatter.Covers(s, clear, 1920, 1080)) < 20);
    }

    [Fact]
    public void A_circle_touching_an_area_covers_it_and_one_beside_it_does_not()
    {
        Rect[] clear = [new(0, 0, 0.5, 0.5)];
        Assert.True(CircleScatter.Covers(new CircleSpot(new Point(110, 50), 15, CircleKind.Disc, 0.2), clear, 200, 200));
        Assert.False(CircleScatter.Covers(new CircleSpot(new Point(150, 150), 20, CircleKind.Disc, 0.2), clear, 200, 200));
    }

    [Fact]
    public void Every_kind_turns_up()
    {
        var kinds = Enumerable.Range(0, 100).Select(i => CircleScatter.KindAt(i / 100.0)).Distinct();
        Assert.Equal(Enum.GetValues<CircleKind>().Length, kinds.Count());
        Assert.Equal(CircleKind.Donut, CircleScatter.KindAt(0.999999));
    }

    [Fact]
    public void Every_kind_is_drawn_round_its_centre_at_its_size()
    {
        Assert.All(Enum.GetValues<CircleKind>(), kind =>
        {
            var bounds = CircleScatter.Shape(kind, 50, 2, 3).Bounds;
            Assert.Equal(-50, bounds.Left, 3);
            Assert.Equal(100, bounds.Width, 3);
            Assert.Equal(100, bounds.Height, 3);
        });
    }
}
