using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What is behind the cards in the Old School layout's Console space look: a deep green lit from the top
/// left, faint circles that pop in and out across it as on the Xbox 360's NXE dashboard (CircleField),
/// motes drifting up through it, and a cream floor that the cards stand on and are reflected in. It
/// sits between the backdrop and the cards, and takes no input. The circles come and go and the motes
/// drift only while the library is in front and the player has the moving background on; each layer of
/// motes moves as one shape with one transform, so a frame is a few pictures moved, not hundreds laid out.
/// </summary>
internal sealed class ConsoleSpaceLayer : Grid
{
    private static readonly Color Mote = Color.FromRgb(0xC8, 0xFF, 0xA0);

    // Two layers of motes, the far one smaller, fainter and slower.
    private static readonly (int Count, double Biggest, double Brightest, double Seconds)[] MoteLayers =
    [
        (45, 2.2, 0.5, 90),
        (40, 1.3, 0.32, 140)
    ];

    // The circles keep out of the list of categories at the top left and the clock at the top right, where
    // they made the words hard to read on the lime.
    private readonly CircleField _circles = new() { KeepClear = [new Rect(0, 0, 0.32, 0.36), new Rect(0.8, 0, 0.2, 0.08)] };
    private readonly Canvas _motes = new() { ClipToBounds = false };
    private readonly Rectangle _floor = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly List<(UIElement Element, DependencyProperty Property, AnimationTimeline Move)> _moves = [];
    private Size _builtFor;
    private bool _moving;
    private (double Horizon, double Contact) _floorAt = (-1, -1);

    public ConsoleSpaceLayer()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        LimeBackdrop.AddTo(this);

        Children.Add(_circles);
        Children.Add(_motes);
        Children.Add(_floor);

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
        // The circles stay, still, with the moving background off; the motes only show while they drift.
        _motes.Visibility = shown && LibrarySettings.MovingBackground ? Visibility.Visible : Visibility.Collapsed;
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
        // Cream, which the user asked for on 2026-11-13, in place of the dark glass.
        _floor.Fill = Frozen(new LinearGradientBrush(
            [new GradientStop(Color.FromArgb(0, 0xF4, 0xEE, 0xD8), 0), new GradientStop(Color.FromArgb(0x8C, 0xF4, 0xEE, 0xD8), at),
             new GradientStop(Color.FromArgb(0xE0, 0xF2, 0xEB, 0xD2), at + ((1 - at) * 0.5)), new GradientStop(Color.FromArgb(0xF5, 0xEF, 0xE7, 0xCC), 1)], 90));
    }

    /// <summary>
    /// Makes the circles and motes for this size of screen. The circles land somewhere new each time; the
    /// motes' loops are one screen long so they join up seamlessly.
    /// </summary>
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
        _motes.Children.Clear();
        _circles.Build(size);

        var (width, height) = (size.Width, size.Height);
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
        // the cards over it, glow and reflection, so half the frames is half that work. Set here, as
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

        if (moving)
        {
            _circles.Start();
        }
        else
        {
            _circles.Stop();
        }
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The lime of the Xbox 360's NXE dashboard, shared by the Console space look and the stats screen: a lime
/// green that brightens to yellow a little above and right of the middle, and deepens to green at the edges.
/// </summary>
internal static class LimeBackdrop
{
    private static readonly Brush[] Layers =
    [
        Frozen(new LinearGradientBrush(
            [new GradientStop(Color.FromRgb(0x78, 0xA6, 0x08), 0), new GradientStop(Color.FromRgb(0x92, 0xBC, 0x04), 0.5),
             new GradientStop(Color.FromRgb(0x5A, 0x88, 0x08), 1)], 90)),
        Frozen(new RadialGradientBrush
        {
            Center = new Point(0.56, 0.4),
            GradientOrigin = new Point(0.56, 0.4),
            RadiusX = 0.5,
            RadiusY = 0.55,
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0xE0, 0xE4, 0xE2, 0x00), 0),
                new GradientStop(Color.FromArgb(0x60, 0xC8, 0xDC, 0x00), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xC8, 0xDC, 0x00), 1)
            ]
        }),

        // Deeper towards the edges and corners, as the dashboard's was.
        Frozen(new RadialGradientBrush
        {
            Center = new Point(0.5, 0.45),
            GradientOrigin = new Point(0.5, 0.45),
            RadiusX = 0.8,
            RadiusY = 0.8,
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0x00, 0x2E, 0x48, 0x00), 0.5),
                new GradientStop(Color.FromArgb(0x70, 0x2E, 0x48, 0x00), 1)
            ]
        })
    ];

    /// <summary>Adds the lime's layers to the back of a panel, one rectangle each.</summary>
    public static void AddTo(Panel panel)
    {
        for (var i = 0; i < Layers.Length; i++)
        {
            panel.Children.Insert(i, new Rectangle { Fill = Layers[i], IsHitTestVisible = false });
        }
    }

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
        // White, faded by the list's own fading, as the NXE dashboard's names were on its lime.
        resources["CategoryBrush"] = Brush(Color.FromRgb(0xFA, 0xFF, 0xF0));
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
