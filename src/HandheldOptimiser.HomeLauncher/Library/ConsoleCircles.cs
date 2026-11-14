using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The kinds of circle in the Console space background, as the NXE dashboard's background had them.</summary>
internal enum CircleKind
{
    /// <summary>A solid disc.</summary>
    Disc,

    /// <summary>A thin ring.</summary>
    Ring,

    /// <summary>Two to four thin rings round one centre.</summary>
    Rings,

    /// <summary>A thin ring with a dot in its middle.</summary>
    Target,

    /// <summary>A solid disc with a small hole in its middle.</summary>
    Holed,

    /// <summary>A thick ring.</summary>
    Donut
}

/// <summary>One circle of the background: where it is, how big, what kind and how strongly it shows at its brightest.</summary>
internal readonly record struct CircleSpot(Point Centre, double Radius, CircleKind Kind, double Peak);

/// <summary>
/// Where the Console space background's circles appear, and what each looks like. Most gather round a few
/// places on the screen and the rest are anywhere; most are small and a few are large. Pure, so it is tested.
/// </summary>
internal static class CircleScatter
{
    /// <summary>How many places the circles gather round.</summary>
    public const int Clusters = 4;

    // How many of the circles gather round those places rather than landing anywhere.
    private const double Gathered = 0.65;

    // The smallest and largest circle's radius on a screen 1080 high. Sizes lean hard to the small end.
    public const double Smallest = 4;
    public const double Largest = 135;

    // How likely each kind is, in the order of CircleKind.
    private static readonly double[] Chances = [0.3, 0.2, 0.15, 0.2, 0.1, 0.05];

    /// <summary>The places the circles gather round, anywhere across and in the upper three quarters.</summary>
    public static Point[] Places(Random random, double width, double height) =>
        [.. Enumerable.Range(0, Clusters).Select(_ => new Point(random.NextDouble() * width, random.NextDouble() * height * 0.75))];

    // How many times a circle is placed again before one landing on a clear area is let be.
    private const int Tries = 12;

    /// <summary>
    /// A circle somewhere on a screen of that size, near one of the places or, less often, anywhere, and
    /// clear of the areas to keep clear, such as words it would make hard to read.
    /// </summary>
    /// <param name="clear">Areas to keep clear, as parts of the screen from 0 to 1 each way.</param>
    public static CircleSpot Pick(Random random, double width, double height, IReadOnlyList<Point> places, IReadOnlyList<Rect>? clear = null)
    {
        var spot = PickAnywhere(random, width, height, places);
        for (var i = 1; i < Tries && clear is { Count: > 0 } && Covers(spot, clear, width, height); i++)
        {
            spot = PickAnywhere(random, width, height, places);
        }

        return spot;
    }

    /// <summary>Whether a circle reaches into any of the areas to keep clear.</summary>
    public static bool Covers(CircleSpot spot, IReadOnlyList<Rect> clear, double width, double height)
    {
        var circle = new Rect(spot.Centre.X - spot.Radius, spot.Centre.Y - spot.Radius, spot.Radius * 2, spot.Radius * 2);
        return clear.Any(area => circle.IntersectsWith(new Rect(area.X * width, area.Y * height, area.Width * width, area.Height * height)));
    }

    private static CircleSpot PickAnywhere(Random random, double width, double height, IReadOnlyList<Point> places)
    {
        var scale = height / 1080;
        Point centre;
        if (places.Count > 0 && random.NextDouble() < Gathered)
        {
            var place = places[random.Next(places.Count)];
            var spread = width * 0.08;
            centre = new Point(Math.Clamp(place.X + (Gauss(random) * spread), 0, width),
                Math.Clamp(place.Y + (Gauss(random) * spread * 0.6), 0, height));
        }
        else
        {
            centre = new Point(random.NextDouble() * width, random.NextDouble() * height);
        }

        var radius = (Smallest + ((Largest - Smallest) * Math.Pow(random.NextDouble(), 3))) * scale;
        var kind = KindAt(random.NextDouble());

        // A line is only a pixel or two wide, so the rings show more strongly than the solid kinds.
        var peak = kind is CircleKind.Ring or CircleKind.Rings or CircleKind.Target
            ? 0.22 + (random.NextDouble() * 0.16)
            : 0.1 + (random.NextDouble() * 0.12);
        return new CircleSpot(centre, radius, kind, peak);
    }

    /// <summary>The kind a number from 0 to 1 falls on, by each kind's chance.</summary>
    public static CircleKind KindAt(double chance)
    {
        for (var i = 0; i < Chances.Length; i++)
        {
            chance -= Chances[i];
            if (chance < 0)
            {
                return (CircleKind)i;
            }
        }

        return CircleKind.Disc;
    }

    /// <summary>The shape of a circle round the point 0,0, filled: rings are drawn as thin bands, not outlines.</summary>
    /// <param name="line">How wide a thin ring's line is.</param>
    /// <param name="rings">How many rings a <see cref="CircleKind.Rings"/> has.</param>
    public static Geometry Shape(CircleKind kind, double radius, double line, int rings)
    {
        Geometry shape = kind switch
        {
            CircleKind.Disc => Circle(radius),
            CircleKind.Ring => Band(radius, line),
            CircleKind.Rings => Group([.. Enumerable.Range(1, rings).Select(i => Band(radius * i / rings, line))]),
            CircleKind.Target => Group(Band(radius, line), Circle(radius * 0.28)),
            CircleKind.Holed => new CombinedGeometry(GeometryCombineMode.Exclude, Circle(radius), Circle(radius * 0.18)),
            _ => Band(radius, radius * 0.35)
        };
        shape.Freeze();
        return shape;
    }

    private static EllipseGeometry Circle(double radius) => new(new Point(0, 0), radius, radius);

    private static CombinedGeometry Band(double radius, double width) =>
        new(GeometryCombineMode.Exclude, Circle(radius), Circle(Math.Max(0, radius - width)));

    private static GeometryGroup Group(params Geometry[] parts)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }

        return group;
    }

    /// <summary>A number from the normal spread round 0, most within 1 either side.</summary>
    private static double Gauss(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}

/// <summary>
/// The circles of the Console space background. Each fades and pops in somewhere, stays a few seconds,
/// fades out and comes back somewhere else, each in its own time, so the pattern never repeats. With the
/// moving background off they stay where they are. How strongly one shows is animated on its brush, not
/// the shape: a shape with an opacity of its own is drawn into a surface of its own first.
/// </summary>
internal sealed class CircleField : Canvas
{
    private const int Count = 46;
    private static readonly Color Pale = Color.FromRgb(0xE6, 0xFF, 0xB4);

    private readonly Random _random = new();
    private readonly List<(Path Shape, SolidColorBrush Brush, ScaleTransform Scale)> _circles = [];
    private readonly Dictionary<Path, double> _peaks = [];
    private Point[] _places = [];
    private Size _size;
    private bool _moving;

    // Which build the circles are from, so a circle of an earlier one finishing does not come back.
    private int _build;

    public CircleField() => IsHitTestVisible = false;

    /// <summary>Areas the circles keep out of, as parts of the field from 0 to 1 each way. Set before it is built.</summary>
    public IReadOnlyList<Rect> KeepClear { get; init; } = [];

    /// <summary>Makes the circles for a screen of that size, somewhere new each time.</summary>
    public void Build(Size size)
    {
        var wasMoving = _moving;
        Stop();
        _build++;
        _size = size;
        _places = CircleScatter.Places(_random, size.Width, size.Height);
        Children.Clear();
        _circles.Clear();
        _peaks.Clear();

        for (var i = 0; i < Count; i++)
        {
            var brush = new SolidColorBrush(Pale);
            var scale = new ScaleTransform();
            var shape = new Path { Fill = brush, RenderTransform = scale };
            Children.Add(shape);
            _circles.Add((shape, brush, scale));
            Place(shape);
            brush.Opacity = _peaks[shape];
        }

        if (wasMoving)
        {
            Start();
        }
    }

    /// <summary>Starts the circles coming and going, each after a wait of its own so they do not all arrive at once.</summary>
    public void Start()
    {
        if (_moving)
        {
            return;
        }

        _moving = true;
        foreach (var circle in _circles)
        {
            circle.Brush.Opacity = 0;
            Pop(circle, TimeSpan.FromSeconds(_random.NextDouble() * 6), _build);
        }
    }

    /// <summary>Stops them where they are, each shown at its own strength.</summary>
    public void Stop()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        foreach (var (shape, brush, scale) in _circles)
        {
            brush.BeginAnimation(Brush.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            brush.Opacity = _peaks[shape];
        }
    }

    /// <summary>Gives a circle a new place, size and kind.</summary>
    private void Place(Path shape)
    {
        var spot = CircleScatter.Pick(_random, _size.Width, _size.Height, _places, KeepClear);
        var line = Math.Max(1.2, 2 * _size.Height / 1080);
        shape.Data = CircleScatter.Shape(spot.Kind, spot.Radius, line, 2 + _random.Next(3));
        SetLeft(shape, spot.Centre.X);
        SetTop(shape, spot.Centre.Y);
        _peaks[shape] = spot.Peak;
    }

    /// <summary>One coming and going of a circle: it pops in, stays, fades out, and then comes back elsewhere.</summary>
    private void Pop((Path Shape, SolidColorBrush Brush, ScaleTransform Scale) circle, TimeSpan wait, int build)
    {
        var (shape, brush, scale) = circle;
        var peak = _peaks[shape];
        var appear = 0.8 + (_random.NextDouble() * 0.8);
        var stay = appear + 3 + (_random.NextDouble() * 6);
        var gone = stay + 1.2 + (_random.NextDouble() * 1.2);

        var shine = new DoubleAnimationUsingKeyFrames { BeginTime = wait };
        shine.KeyFrames.Add(new LinearDoubleKeyFrame(0, Time(0)));
        shine.KeyFrames.Add(new EasingDoubleKeyFrame(peak, Time(appear), new SineEase { EasingMode = EasingMode.EaseOut }));
        shine.KeyFrames.Add(new LinearDoubleKeyFrame(peak, Time(stay)));
        shine.KeyFrames.Add(new EasingDoubleKeyFrame(0, Time(gone), new SineEase { EasingMode = EasingMode.EaseIn }));

        // It grows a little past its size as it pops in, settles, and swells a touch as it fades.
        var grow = new DoubleAnimationUsingKeyFrames { BeginTime = wait };
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(0.6, Time(0)));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, Time(appear), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }));
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1, Time(stay)));
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1.1, Time(gone)));

        // Slow enough that 30 frames a second look the same as 60, and every frame redraws the cards over it.
        Timeline.SetDesiredFrameRate(shine, 30);
        Timeline.SetDesiredFrameRate(grow, 30);
        shine.Completed += (_, _) =>
        {
            if (_moving && build == _build)
            {
                Place(shape);
                Pop(circle, TimeSpan.FromSeconds(0.3 + (_random.NextDouble() * 3)), build);
            }
        };

        brush.BeginAnimation(Brush.OpacityProperty, shine);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static KeyTime Time(double seconds) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
}
