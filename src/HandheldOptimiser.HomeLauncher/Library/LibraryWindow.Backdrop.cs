using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The focused game's colours or artwork behind the grid.</summary>
public partial class LibraryWindow
{
    // Long enough that holding a direction across a row does not load every game passed on the way.
    private static readonly TimeSpan BackdropDelay = TimeSpan.FromMilliseconds(150);
    private static readonly Duration BackdropFade = TimeSpan.FromMilliseconds(200);

    // The game whose colours or artwork are behind the grid, or on their way there.
    private string? _backdropKey;
    private CancellationTokenSource? _backdropLoad;

    /// <summary>
    /// Puts the game's colours, or its artwork when the player chose that, behind the grid once the
    /// focus has settled on it. Artwork falls back to the colours for a game with none.
    /// </summary>
    private void QueueBackdrop(GameTile tile)
    {
        if (tile.Game.Key == _backdropKey || !IsActive)
        {
            return;
        }

        _backdropKey = tile.Game.Key;
        _backdropLoad?.Cancel();
        _backdropLoad = new CancellationTokenSource();
        _ = LoadBackdropAsync(tile, _backdropLoad.Token);
    }

    /// <summary>Shows the focused game's background again, after a setting that affects it changed.</summary>
    private void ReloadBackdrop()
    {
        _backdropKey = null;
        if (_tiles.Find(t => t.Game.Key == _focusedKey) is { } tile)
        {
            QueueBackdrop(tile);
        }
    }

    private async Task LoadBackdropAsync(GameTile tile, CancellationToken superseded)
    {
        try
        {
            await Task.Delay(BackdropDelay, superseded);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var background = _background;
        var blur = _blur;
        var position = _position;
        var (width, height) = BackdropPixels();
        var (from, to) = tile.BackdropColours;
        var shade = _strength switch
        {
            ColourStrength.Subtle => 1,
            ColourStrength.Strong => 0.55,
            _ => 0.7
        };

        // Built at the screen's own size off the UI thread, so showing it is only a copy.
        var backdrop = await Task.Run(async () =>
        {
            if (background == BackgroundKind.Plain)
            {
                return null;
            }

            // Apps take their icon's colours, as there is no artwork to look up for them.
            if (background == BackgroundKind.Artwork && !tile.IsApp && await Artwork.FindAsync(tile.Game) is { } path
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
            ShowBackdrop(backdrop);
        }
    }

    /// <summary>The backdrop layer's size in real pixels, which is what the picture is made at.</summary>
    private (int Width, int Height) BackdropPixels()
    {
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = (int)Math.Round(BackdropFront.ActualWidth * toDevice.M11);
        var height = (int)Math.Round(BackdropFront.ActualHeight * toDevice.M22);

        // Before the first layout pass there is no size yet; any sensible one will do until the next.
        return width > 0 && height > 0 ? (width, height) : (1920, 1080);
    }

    /// <summary>
    /// Fades the new background in over the old, which stays solid underneath: one see-through layer
    /// costs far less to draw than two. Null fades to the plain background.
    /// </summary>
    private void ShowBackdrop(ImageSource? backdrop)
    {
        Brush? brush = null;
        if (backdrop is not null)
        {
            brush = new ImageBrush(backdrop) { Stretch = Stretch.Fill };
            brush.Freeze();
        }

        var previous = BackdropFront.Fill;
        if (brush is null && previous is null)
        {
            return;
        }

        BackdropBack.BeginAnimation(OpacityProperty, null);
        BackdropFront.BeginAnimation(OpacityProperty, null);

        if (brush is null)
        {
            // Nothing new to show: the old one fades away on the back layer.
            BackdropFront.Fill = null;
            BackdropFront.Opacity = 0;
            BackdropBack.Fill = previous;
            BackdropBack.BeginAnimation(OpacityProperty, Fade(1, 0, () => BackdropBack.Fill = null));
            return;
        }

        BackdropBack.Fill = previous;
        BackdropBack.Opacity = previous is null ? 0 : 1;
        BackdropFront.Fill = brush;

        // Once the new one is solid the old one is covered, and can go.
        BackdropFront.BeginAnimation(OpacityProperty, Fade(0, 1, () =>
        {
            if (ReferenceEquals(BackdropFront.Fill, brush))
            {
                BackdropBack.Fill = null;
                BackdropBack.Opacity = 0;
            }
        }));
    }

    private static DoubleAnimation Fade(double from, double to, Action done)
    {
        var fade = new DoubleAnimation(from, to, BackdropFade);

        // Software rendering redraws the whole window for each step, so take fewer of them.
        Timeline.SetDesiredFrameRate(fade, 30);
        fade.Completed += (_, _) => done();
        return fade;
    }

    /// <summary>Lets go of the background while a game is in front, along with the rest of the memory.</summary>
    private void ClearBackdrop()
    {
        _backdropLoad?.Cancel();
        _backdropKey = null;

        foreach (var layer in new[] { BackdropBack, BackdropFront })
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
            layer.Fill = null;
        }
    }
}
