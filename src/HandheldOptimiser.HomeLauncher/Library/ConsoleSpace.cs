using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What is behind the cards in the Old School layout's Console space look: a deep green lit from the top
/// left, sweeping lines of light and motes drifting up through it, and a dark glass floor that the cards
/// stand on and are reflected in. It sits between the backdrop and the cards, and takes no input.
/// The lines and motes move only while the library is in front and the player has them on; each moves as
/// one shape with one transform, so a frame is a few pictures moved, not hundreds of things laid out.
/// </summary>
internal sealed class ConsoleSpaceLayer : Grid
{
    private static readonly Color Light = Color.FromRgb(0xB8, 0xFF, 0x7A);
    private static readonly Color Mote = Color.FromRgb(0xC8, 0xFF, 0xA0);

    // The bands of lines: how far down the screen, how high they sway, how many lines, how far apart,
    // where they start in their sway, and how many seconds they take to drift one screen across.
    private static readonly (double Down, double Sway, int Lines, double Apart, double Phase, double Seconds)[] Bands =
    [
        (0.33, 0.11, 6, 0.012, 0.0, 46),
        (0.52, 0.083, 5, 0.015, 2.1, -64),
        (0.23, 0.15, 3, 0.02, 4.0, 82)
    ];

    // Two layers of motes, the far one smaller, fainter and slower.
    private static readonly (int Count, double Biggest, double Brightest, double Seconds)[] MoteLayers =
    [
        (45, 2.2, 0.5, 90),
        (40, 1.3, 0.32, 140)
    ];

    private readonly Canvas _lines = new() { ClipToBounds = false };
    private readonly Canvas _motes = new() { ClipToBounds = false };
    private readonly Rectangle _floor = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Rectangle _floorLine = new() { Height = 1, VerticalAlignment = VerticalAlignment.Top };
    private readonly Rectangle _floorGlow = new() { Height = 22, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<(UIElement Element, DependencyProperty Property, AnimationTimeline Move)> _moves = [];
    private Size _builtFor;
    private bool _moving;
    private (double Horizon, double Contact) _floorAt = (-1, -1);

    public ConsoleSpaceLayer()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        // The deep green, lit from the top left and a little from the right.
        Children.Add(new Rectangle { Fill = Frozen(Vertical(
            (0, Color.FromRgb(0x1D, 0x4A, 0x12)), (0.45, Color.FromRgb(0x10, 0x30, 0x0A)),
            (0.7, Color.FromRgb(0x07, 0x16, 0x06)), (1, Color.FromRgb(0x03, 0x08, 0x03)))) });
        Children.Add(new Rectangle { Fill = Glow(new Point(0.18, 0.12), 0.7, 0.6, Color.FromArgb(0xFF, 0x4F, 0x8A, 0x22)) });
        Children.Add(new Rectangle { Fill = Glow(new Point(0.85, 0.3), 0.6, 0.5, Color.FromArgb(0x8C, 0x28, 0x6E, 0x1E)) });
        Children.Add(Beams());

        Children.Add(_lines);
        Children.Add(_motes);
        Children.Add(_floor);
        Children.Add(_floorGlow);
        Children.Add(_floorLine);

        _floorLine.Fill = Frozen(Across((0, 0), (0.25, 0.35), (0.7, 0.08), (1, 0)));
        _floorGlow.Fill = Frozen(new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, 0x8C, 0xF0, 0x64), 0), new GradientStop(Color.FromArgb(0x24, 0x8C, 0xF0, 0x64), 0.5),
             new GradientStop(Color.FromArgb(0, 0x8C, 0xF0, 0x64), 1)], 90));

        SizeChanged += (_, _) => Build();
    }

    /// <summary>
    /// Shows the space or hides it, and starts or stops what moves in it.
    /// </summary>
    /// <param name="shown">Whether the Console space look is on screen.</param>
    /// <param name="moving">Whether the library is in front. Behind a game the space is not drawn at all, so nothing of it is kept.</param>
    public void Show(bool shown, bool moving)
    {
        Visibility = shown && moving ? Visibility.Visible : Visibility.Collapsed;
        _lines.Visibility = _motes.Visibility = shown && LibrarySettings.MovingBackground ? Visibility.Visible : Visibility.Collapsed;
        Move(shown && moving && LibrarySettings.MovingBackground);
    }

    /// <summary>
    /// Puts the glass floor where the cards stand: it fades in from the line through the cards' middles,
    /// which is where the stack runs away to, and is dark from the front card's lower edge down.
    /// </summary>
    /// <param name="horizon">The line through the cards' middles, in this layer's units.</param>
    /// <param name="contact">The front card's lower edge.</param>
    public void PlaceFloor(double horizon, double contact)
    {
        if (Math.Abs(horizon - _floorAt.Horizon) < 0.5 && Math.Abs(contact - _floorAt.Contact) < 0.5)
        {
            return;
        }

        _floorAt = (horizon, contact);
        var height = Math.Max(1, ActualHeight - horizon);
        var at = Math.Clamp((contact - horizon) / height, 0.05, 0.95);
        _floor.Margin = new Thickness(0, horizon, 0, 0);
        _floor.Height = height;
        _floor.Fill = Frozen(new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, 2, 8, 2), 0), new GradientStop(Color.FromArgb(0x8C, 2, 8, 2), at),
             new GradientStop(Color.FromArgb(0xE0, 1, 4, 1), at + ((1 - at) * 0.5)), new GradientStop(Color.FromArgb(0xF5, 1, 3, 1), 1)], 90));
        _floorLine.Margin = new Thickness(0, contact, 0, 0);
        _floorGlow.Margin = new Thickness(0, contact - 11, 0, 0);
    }

    /// <summary>Makes the lines and motes for this size of screen, each loop one screen long so it joins up seamlessly.</summary>
    private void Build()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 1 || size.Height < 1 || size == _builtFor)
        {
            return;
        }

        _builtFor = size;
        var wasMoving = _moving;
        Move(false);
        _moves.Clear();
        _lines.Children.Clear();
        _motes.Children.Clear();

        var (width, height) = (size.Width, size.Height);
        foreach (var band in Bands)
        {
            var group = new Canvas { RenderTransform = new TranslateTransform() };

            // A wide, faint stroke under the lines for the glow they give off, as a blur would cost every frame.
            var middle = (band.Down + (band.Lines * band.Apart / 2)) * height;
            group.Children.Add(Line(Wave(middle, band.Sway * height, band.Phase, width), width, 64 * height / 1080, 0.07));
            group.Children.Add(Line(Wave(middle, band.Sway * height, band.Phase, width), width, 22 * height / 1080, 0.06));
            for (var i = 0; i < band.Lines; i++)
            {
                var wave = Wave((band.Down + (i * band.Apart)) * height, band.Sway * height * (1 - (i * 0.04)), band.Phase + (i * 0.09), width);
                group.Children.Add(Line(wave, width, (i == 0 ? 2.2 : 1.2) * height / 1080, 0.55 - (i * 0.07)));
            }

            _lines.Children.Add(group);
            var (from, to) = band.Seconds > 0 ? (0d, -width) : (-width, 0d);
            _moves.Add((group, TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(Math.Abs(band.Seconds)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            }));
        }

        var random = new Random(7);
        foreach (var layer in MoteLayers)
        {
            var group = new Canvas { RenderTransform = new TranslateTransform() };
            for (var i = 0; i < layer.Count; i++)
            {
                var (x, y) = (random.NextDouble() * width, random.NextDouble() * height);
                var radius = (0.4 + (random.NextDouble() * (layer.Biggest - 0.4))) * height / 1080;
                var brush = Frozen(new SolidColorBrush(Mote) { Opacity = 0.15 + (random.NextDouble() * (layer.Brightest - 0.15)) });

                // Each mote twice, a screen apart, so the layer can drift up one screen and start again unseen.
                foreach (var top in new[] { y, y + height })
                {
                    var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = brush };
                    Canvas.SetLeft(dot, x);
                    Canvas.SetTop(dot, top);
                    group.Children.Add(dot);
                }
            }

            _motes.Children.Add(group);
            _moves.Add((group, TranslateTransform.YProperty, new DoubleAnimation(0, -height, TimeSpan.FromSeconds(layer.Seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever
            }));
        }

        // The drift is slow enough that 30 frames a second look the same as 60, and every frame redraws
        // the cards over it, glow, tilt and reflection, so half the frames is half that work. Set here, as
        // an animation that has run is frozen and cannot be changed.
        foreach (var (_, _, move) in _moves)
        {
            Timeline.SetDesiredFrameRate(move, 30);
        }

        if (wasMoving)
        {
            Move(true);
        }

        // The floor's brush is made for the height, so it is made again.
        var floor = _floorAt;
        _floorAt = (-1, -1);
        if (floor.Horizon >= 0)
        {
            PlaceFloor(floor.Horizon, floor.Contact);
        }
    }

    private void Move(bool moving)
    {
        if (moving == _moving)
        {
            return;
        }

        _moving = moving;
        foreach (var (element, property, move) in _moves)
        {
            element.RenderTransform.BeginAnimation(property, moving ? move : null);
        }
    }

    /// <summary>A sway one screen across, drawn over two screens so it can drift one along and join up.</summary>
    private static StreamGeometry Wave(double down, double sway, double phase, double width)
    {
        var wave = new StreamGeometry();
        using (var draw = wave.Open())
        {
            var step = width / 160;
            for (var x = 0d; x <= (width * 2) + step; x += step)
            {
                var y = down + (sway * Math.Sin((x / width * 2 * Math.PI) + phase)) + (sway * 0.15 * Math.Sin((x / width * 6 * Math.PI) + (phase * 2)));
                if (x == 0)
                {
                    draw.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    draw.LineTo(new Point(x, y), true, false);
                }
            }
        }

        wave.Freeze();
        return wave;
    }

    /// <summary>
    /// One line of light, bright in the middle of each screen's width and fading at the ends, which repeat.
    /// How faint it is goes on the brush, not the line: a shape with an opacity of its own is drawn into a
    /// surface of its own first, which for a line two screens long cost about 9 MB each (measured 2026-11-12).
    /// </summary>
    private static Path Line(Geometry wave, double width, double thickness, double opacity) => new()
    {
        Data = wave,
        StrokeThickness = thickness,
        Stroke = Frozen(new LinearGradientBrush
        {
            Opacity = opacity,
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(width, 0),
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0x10, Light.R, Light.G, Light.B), 0),
                new GradientStop(Color.FromArgb(0xFF, Light.R, Light.G, Light.B), 0.35),
                new GradientStop(Color.FromArgb(0xA0, 0x7E, 0xE0, 0x4A), 0.7),
                new GradientStop(Color.FromArgb(0x10, Light.R, Light.G, Light.B), 1)
            ]
        })
    };

    /// <summary>Two soft beams of light falling from beyond the top left corner.</summary>
    private static Path Beams()
    {
        var beams = new PathGeometry();
        foreach (var (from, to) in new[] { (0.17, 0.33), (0.45, 0.58) })
        {
            var figure = new PathFigure { StartPoint = new Point(0.06, -0.1), IsClosed = true };
            figure.Segments.Add(new LineSegment(new Point(1.2 * Math.Cos(from), 1.2 * Math.Sin(from) * 1.6), false));
            figure.Segments.Add(new LineSegment(new Point(1.2 * Math.Cos(to), 1.2 * Math.Sin(to) * 1.6), false));
            beams.Figures.Add(figure);
        }

        return new Path
        {
            Data = beams,
            Stretch = Stretch.Fill,
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x1C, 0xBE, 0xFF, 0x8C), Color.FromArgb(0, 0xBE, 0xFF, 0x8C))
            {
                Center = new Point(0, 0),
                GradientOrigin = new Point(0, 0),
                RadiusX = 1,
                RadiusY = 1
            })
        };
    }

    private static RadialGradientBrush Glow(Point centre, double across, double down, Color colour) => Frozen(new RadialGradientBrush(
        colour, Color.FromArgb(0, colour.R, colour.G, colour.B))
    {
        Center = centre,
        GradientOrigin = centre,
        RadiusX = across,
        RadiusY = down
    });

    private static LinearGradientBrush Vertical(params (double At, Color Colour)[] stops) =>
        new([.. stops.Select(s => new GradientStop(s.Colour, s.At))], 90);

    /// <summary>A line of the floor's light: its strength across the screen, from the left.</summary>
    private static LinearGradientBrush Across(params (double At, double Strength)[] stops) =>
        new([.. stops.Select(s => new GradientStop(Color.FromArgb((byte)(s.Strength * 255), 0xA0, 0xFF, 0x78), s.At))], 0);

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Switches the Console space look's touches on the library's own parts, which the XAML reaches through
/// dynamic resources: the cards' glow, gloss, shadows and reflections, the categories' font and fading,
/// the glass capsule round the clock and the glass pills of the button hints. Classic puts every one back
/// as it was, so the Classic look and the other layouts are drawn exactly as before.
/// </summary>
internal static class ConsoleSpaceLook
{
    // The one font of the Console space look: Windows' own sharp, geometric Bahnschrift.
    private static readonly FontFamily Geometric = new("Bahnschrift, Segoe UI");

    /// <summary>Whether the look is on now.</summary>
    public static bool On { get; private set; }

    // Every resource the look sets. With the look off they are taken away again, and the values in
    // LibraryResources.xaml, which are the Classic look's, show through.
    private static readonly string[] Keys =
    [
        "ConsoleOnly", "ClassicOnly", "CardTitleFont", "CategoryFont", "HintFont", "CategoryBrush", "CardRingBrush",
        "CardRingEffect", "HudBackground", "HudBorder", "HudPadding", "HudEnd", "PillBackground", "PillBorder",
        "PillPadding", "PillGap", "AcceptPillBackground", "AcceptPillBorder", "AcceptGlow"
    ];

    /// <param name="on">Whether the Old School layout is on screen in the Console space look.</param>
    public static void Apply(bool on)
    {
        if (Application.Current is not { } app || on == On)
        {
            return;
        }

        On = on;
        var resources = app.Resources;
        if (!on)
        {
            foreach (var key in Keys)
            {
                resources.Remove(key);
            }

            return;
        }

        resources["ConsoleOnly"] = Visibility.Visible;
        resources["ClassicOnly"] = Visibility.Collapsed;
        resources["CardTitleFont"] = Geometric;
        resources["CategoryFont"] = Geometric;
        resources["HintFont"] = Geometric;
        resources["CategoryBrush"] = Brush(Color.FromRgb(0xC8, 0xD0, 0xC8));
        resources["CardRingBrush"] = Brush(Color.FromRgb(0xEC, 0xFF, 0xE0));
        resources["CardRingEffect"] = Glow(Color.FromRgb(0x6E, 0xDC, 0x3C), 38, 0.95);

        // The capsule round the clock and the pills of the button hints: tinted glass with a light edge.
        var glass = Brush(Color.FromArgb(0x6B, 0x0C, 0x14, 0x0C));
        var edge = Brush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
        resources["HudBackground"] = glass;
        resources["HudBorder"] = edge;
        resources["HudPadding"] = new Thickness(20, 7, 0, 7);
        resources["HudEnd"] = new Thickness(0, 0, 20, 0);
        resources["PillBackground"] = glass;
        resources["PillBorder"] = edge;
        resources["PillPadding"] = new Thickness(6, 6, 18, 6);
        resources["PillGap"] = new Thickness(12, 0, 0, 0);
        resources["AcceptPillBackground"] = Brush(Color.FromArgb(0x73, 0x28, 0x5A, 0x14));
        resources["AcceptPillBorder"] = Brush(Color.FromArgb(0x59, 0x96, 0xF0, 0x6E));
        resources["AcceptGlow"] = Glow(Color.FromRgb(0x6E, 0xE6, 0x3C), 16, 0.9);
    }

    private static SolidColorBrush Brush(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect Glow(Color colour, double blur, double opacity)
    {
        var glow = new DropShadowEffect { Color = colour, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
        glow.Freeze();
        return glow;
    }
}
