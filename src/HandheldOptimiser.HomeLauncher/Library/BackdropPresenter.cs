using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What is behind the library: the focused game's colours or artwork, the Xbox 360's green behind the
/// categories that have no game to take colours from, or nothing. Two layers, so a new picture fades in
/// over the old.
/// </summary>
/// <param name="front">The layer a new picture fades in on.</param>
/// <param name="back">The layer the old one stays solid on underneath.</param>
/// <param name="keepsGreen">
/// Whether the green stays whichever game is picked, as it does around the open Not installed card.
/// </param>
internal sealed class BackdropPresenter(Rectangle front, Rectangle back, LibraryState state, LibraryOptions options,
    LibraryNavigation navigation, Func<bool> keepsGreen)
{
    /// <summary>
    /// How long the focus has to rest on a game before its picture is made. Long enough that holding a
    /// direction across a row does not load every game passed on the way.
    /// </summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    private static readonly Duration FadeTime = TimeSpan.FromMilliseconds(200);

    // The green of the Xbox 360's own cards.
    private static readonly Color Green = Color.FromRgb(0x86, 0xB5, 0x1C);
    private static readonly Color DeepGreen = Color.FromRgb(0x24, 0x4A, 0x08);
    private const string GreenKey = "category:green";

    // The game whose colours or artwork are behind the library, or on their way there.
    private string? _key;
    private CancellationTokenSource? _load;

    /// <summary>
    /// Puts the game's colours, or its artwork when the player chose that, behind the library once the
    /// focus has settled on it. Artwork falls back to the colours for a game with none.
    /// </summary>
    public void Queue(GameTile tile)
    {
        // The open Not installed card keeps its green around it, whichever game is picked, and so does
        // every game when the player chose the green for the background.
        if (keepsGreen() || options.Background == BackgroundKind.Green)
        {
            ShowGreen();
            return;
        }

        if (tile.Game.Key == _key || !navigation.InFront)
        {
            return;
        }

        _key = tile.Game.Key;
        _load?.Cancel();
        _load = new CancellationTokenSource();
        _ = LoadAsync(tile, _load.Token);
    }

    /// <summary>Shows the focused game's background again, after a setting that affects it changed.</summary>
    public void Reload()
    {
        _key = null;
        if (state.FocusedTile is { } tile)
        {
            Queue(tile);
        }
    }

    /// <summary>Forgets which game is behind the library, so the next focus loads it again.</summary>
    public void Forget() => _key = null;

    /// <summary>The Xbox 360's green behind the categories that have no game to take colours from.</summary>
    public async void ShowGreen()
    {
        if (_key == GreenKey || !navigation.InFront || options.Background == BackgroundKind.Plain)
        {
            return;
        }

        _key = GreenKey;
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();

        var (width, height) = Pixels();
        var shade = Shade(options.Strength);
        var backdrop = await Task.Run(() => Backdrops.Gradient(Green, DeepGreen, width, height, shade));
        if (!load.IsCancellationRequested)
        {
            Show(backdrop);
        }
    }

    /// <summary>Lets go of the background while a game is in front, along with the rest of the memory.</summary>
    public void Clear()
    {
        _load?.Cancel();
        _key = null;

        foreach (var layer in new[] { back, front })
        {
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Opacity = 0;
            layer.Fill = null;
        }
    }

    private static double Shade(ColourStrength strength) => strength switch
    {
        ColourStrength.Subtle => 1,
        ColourStrength.Strong => 0.55,
        _ => 0.7
    };

    private async Task LoadAsync(GameTile tile, CancellationToken superseded)
    {
        try
        {
            await Task.Delay(SettleDelay, superseded);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var background = options.Background;
        var blur = options.Blur;
        var position = options.Position;
        var (width, height) = Pixels();
        var (from, to) = tile.BackdropColours;
        var shade = Shade(options.Strength);

        // Built at the screen's own size off the UI thread, so showing it is only a copy.
        var backdrop = await Task.Run(async () =>
        {
            if (background == BackgroundKind.Plain)
            {
                return null;
            }

            if (background == BackgroundKind.Artwork && await Artwork.FindAsync(tile.Game) is { } path
                && Backdrops.FromArtwork(path, width, height, blur, position, from, to, shade) is { } artwork)
            {
                // Decoding artwork (up to 4K) leaves tens of MB of buffers behind, some of them outside
                // the managed heap where only a collection frees them. Left alone they pile up with
                // every game passed, in a process that is meant to stay small.
                GC.Collect();
                return artwork;
            }

            return Backdrops.Gradient(from, to, width, height, shade);
        });

        if (!superseded.IsCancellationRequested)
        {
            Show(backdrop);
        }
    }

    /// <summary>The backdrop layer's size in real pixels, which is what the picture is made at.</summary>
    private (int Width, int Height) Pixels()
    {
        var toDevice = PresentationSource.FromVisual(front)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = (int)Math.Round(front.ActualWidth * toDevice.M11);
        var height = (int)Math.Round(front.ActualHeight * toDevice.M22);

        // Before the first layout pass there is no size yet; any sensible one will do until the next.
        return width > 0 && height > 0 ? (width, height) : (1920, 1080);
    }

    /// <summary>
    /// Fades the new background in over the old, which stays solid underneath: one see-through layer
    /// costs far less to draw than two. Null fades to the plain background.
    /// </summary>
    private void Show(ImageSource? backdrop)
    {
        Brush? brush = null;
        if (backdrop is not null)
        {
            brush = new ImageBrush(backdrop) { Stretch = Stretch.Fill };
            brush.Freeze();
        }

        var previous = front.Fill;
        if (brush is null && previous is null)
        {
            return;
        }

        back.BeginAnimation(UIElement.OpacityProperty, null);
        front.BeginAnimation(UIElement.OpacityProperty, null);

        if (brush is null)
        {
            // Nothing new to show: the old one fades away on the back layer.
            front.Fill = null;
            front.Opacity = 0;
            back.Fill = previous;
            back.BeginAnimation(UIElement.OpacityProperty, Fade(1, 0, () => back.Fill = null));
            return;
        }

        back.Fill = previous;
        back.Opacity = previous is null ? 0 : 1;
        front.Fill = brush;

        // Once the new one is solid the old one is covered, and can go.
        front.BeginAnimation(UIElement.OpacityProperty, Fade(0, 1, () =>
        {
            if (ReferenceEquals(front.Fill, brush))
            {
                back.Fill = null;
                back.Opacity = 0;
            }
        }));
    }

    private static DoubleAnimation Fade(double from, double to, Action done)
    {
        var fade = new DoubleAnimation(from, to, FadeTime);

        // Software rendering redraws the whole window for each step, so take fewer of them.
        Timeline.SetDesiredFrameRate(fade, 30);
        fade.Completed += (_, _) => done();
        return fade;
    }
}
