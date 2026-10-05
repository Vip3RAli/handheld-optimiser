using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Builds the picture behind the grid, finished and at the screen's own resolution: blurred, and with the
/// dark shade that keeps tiles and text readable already in it.
///
/// The library draws in software to stay small, and there the cost of a background is in stretching and
/// blending it, on every frame it is on screen. Measured on the Ally, a stretched picture under a
/// separate shade took about 60 ms a frame and twice that while fading; a finished one drawn as it is
/// takes about 6 ms. So the work is done here once, off the UI thread.
/// </summary>
internal static class Backdrops
{
    // The shade from top to bottom: lighter behind the tiles, darker under the title and the hints.
    private static readonly (double Offset, double Alpha)[] ShadeStops = [(0, 0.82), (0.45, 0.66), (1, 0.90)];
    private const double ShadeRed = 0x0F, ShadeGreen = 0x11, ShadeBlue = 0x15;

    // Three passes of a box blur come close to a Gaussian one.
    private const int BlurPasses = 3;

    /// <summary>
    /// How each blur setting is carried out. The softer ones are laid out and blurred at half size, then
    /// enlarged: a quarter of the pixels to work on, and nothing sharper than the blur survives to show
    /// the difference. Off and Light work at full size, where the sharpness is the point.
    /// </summary>
    private static (int Divisor, int Radius) Plan(ArtworkBlur blur) => blur switch
    {
        ArtworkBlur.Off => (1, 0),
        ArtworkBlur.Light => (1, 3),
        ArtworkBlur.Strong => (2, 8),
        _ => (2, 3)
    };

    private const int BytesPerPixel = 4;

    /// <summary>A diagonal gradient between two colours, under the shade.</summary>
    /// <param name="shade">How much of the shade to apply, from 0 to 1.</param>
    public static BitmapSource Gradient(Color from, Color to, int width, int height, double shade) =>
        Frozen(GradientPixels(from, to, width, height, shade), width, height);

    private static byte[] GradientPixels(Color from, Color to, int width, int height, double shade)
    {
        var pixels = new byte[width * height * BytesPerPixel];
        var alphas = ShadeByRow(height, shade);

        // Rows are independent, so they are shared out across the cores: the picture is wanted quickly.
        Parallel.For(0, height, y =>
        {
            var alpha = alphas[y];
            var keep = 1 - alpha;
            var down = (double)y / height;
            var row = y * width * BytesPerPixel;

            for (var x = 0; x < width; x++)
            {
                var t = ((double)x / width + down) / 2;

                // A touch of noise before rounding: dark gradients this wide otherwise show as bands.
                var noise = Noise(x, y);
                var i = row + x * BytesPerPixel;
                pixels[i] = Round((from.B + (to.B - from.B) * t) * keep + ShadeBlue * alpha + noise);
                pixels[i + 1] = Round((from.G + (to.G - from.G) * t) * keep + ShadeGreen * alpha + noise);
                pixels[i + 2] = Round((from.R + (to.R - from.R) * t) * keep + ShadeRed * alpha + noise);
            }
        });

        return pixels;
    }

    /// <summary>
    /// The whole artwork across the top or the bottom of the screen, as sharp or as blurred as the
    /// player chose, fading into the game's colour gradient over the rest. Banner artwork is about three
    /// times as wide as it is tall, so filling a 16:9 screen with it would crop away two fifths of the
    /// picture. Null when the file cannot be read as a picture.
    /// </summary>
    /// <param name="shade">How much of the shade to apply to the gradient, from 0 to 1.</param>
    public static BitmapSource? FromArtwork(string path, int width, int height, ArtworkBlur blur,
        ArtworkPosition position, Color from, Color to, double shade)
    {
        if (Decode(path) is not { } picture)
        {
            return null;
        }

        // As tall as the picture is at the screen's full width, or the whole screen if it is taller.
        var pictureHeight = Math.Min(height, (int)Math.Round((double)width * picture.PixelHeight / picture.PixelWidth));
        var (divisor, radius) = Plan(blur);
        if (pictureHeight < 1 || Soften(picture, width, pictureHeight, divisor, radius) is not { } art)
        {
            return null;
        }

        var pixels = GradientPixels(from, to, width, height, shade);
        var alphas = ShadeByRow(height, PictureShade);

        var atBottom = position == ArtworkPosition.Bottom;
        var firstRow = atBottom ? height - pictureHeight : 0;

        // The picture gives way to the gradient over this many rows, on the side where the two meet.
        var fade = pictureHeight < height ? pictureHeight * FadeShare : 0;

        Parallel.For(0, pictureHeight, y =>
        {
            var fromMeetingEdge = atBottom ? y : pictureHeight - 1 - y;
            var show = fromMeetingEdge >= fade ? 1 : Smooth(fromMeetingEdge / fade);
            var alpha = alphas[firstRow + y];
            var keep = 1 - alpha;
            var source = y * width * BytesPerPixel;
            var target = (firstRow + y) * width * BytesPerPixel;

            for (var x = 0; x < width * BytesPerPixel; x += BytesPerPixel)
            {
                var s = source + x;
                var t = target + x;
                pixels[t] = Round((art[s] * keep + ShadeBlue * alpha) * show + pixels[t] * (1 - show));
                pixels[t + 1] = Round((art[s + 1] * keep + ShadeGreen * alpha) * show + pixels[t + 1] * (1 - show));
                pixels[t + 2] = Round((art[s + 2] * keep + ShadeRed * alpha) * show + pixels[t + 2] * (1 - show));
            }
        });

        return Frozen(pixels, width, height);
    }

    // A picture is the point of this background, so it gets less of the shade than a plain gradient
    // needs to stay out of the way of the text.
    private const double PictureShade = 0.72;

    // The share of the picture's height, on the side that meets the gradient, over which it fades into it.
    private const double FadeShare = 0.4;

    /// <summary>Eases in and out, so the fade has no visible start or end line.</summary>
    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static BitmapSource? Decode(string path)
    {
        try
        {
            // From memory, so the file is not held open: Steam replaces its artwork while we run.
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0 ? frame : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or FormatException or ArgumentException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    /// <summary>The picture covering the given size, blurred. Not yet shaded.</summary>
    private static byte[]? Soften(BitmapSource picture, int width, int height, int divisor, int radius)
    {
        var workWidth = (width + divisor - 1) / divisor;
        var workHeight = (height + divisor - 1) / divisor;

        if (Cover(picture, workWidth, workHeight) is not { } work)
        {
            return null;
        }

        if (radius > 0)
        {
            var scratch = new byte[work.Length];
            for (var pass = 0; pass < BlurPasses; pass++)
            {
                BlurRows(work, scratch, workWidth, workHeight, radius);
                BlurColumns(scratch, work, workWidth, workHeight, radius);
            }
        }

        return divisor == 1 ? work : Enlarge(work, workWidth, workHeight, width, height);
    }

    /// <summary>The picture scaled to cover the given size and cropped to it about its centre.</summary>
    private static byte[]? Cover(BitmapSource picture, int width, int height)
    {
        try
        {
            // A pixel over on each side, so rounding never leaves the crop short of the edge.
            var scale = Math.Max((width + 1.0) / picture.PixelWidth, (height + 1.0) / picture.PixelHeight);
            var scaled = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
            if (scaled.PixelWidth < width || scaled.PixelHeight < height)
            {
                return null;
            }

            var crop = new CroppedBitmap(scaled, new Int32Rect(
                (scaled.PixelWidth - width) / 2, (scaled.PixelHeight - height) / 2, width, height));

            var pixels = new byte[width * height * BytesPerPixel];
            new FormatConvertedBitmap(crop, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, width * BytesPerPixel, 0);
            return pixels;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException
            or InvalidOperationException or OverflowException or COMException)
        {
            return null;
        }
    }

    private static void BlurRows(byte[] source, byte[] target, int width, int height, int radius) =>
        Parallel.For(0, height, y => BlurLine(source, target, y * width * BytesPerPixel, BytesPerPixel, width, radius));

    private static void BlurColumns(byte[] source, byte[] target, int width, int height, int radius) =>
        Parallel.For(0, width, x => BlurLine(source, target, x * BytesPerPixel, width * BytesPerPixel, height, radius));

    /// <summary>
    /// A box blur along one row or column, as a running total so its cost does not grow with the
    /// radius. Past either end, the edge pixel repeats.
    /// </summary>
    private static void BlurLine(byte[] source, byte[] target, int start, int step, int count, int radius)
    {
        var window = radius * 2 + 1;

        for (var channel = 0; channel < 3; channel++)
        {
            var first = start + channel;
            var last = count - 1;

            var total = 0;
            for (var k = -radius; k <= radius; k++)
            {
                total += source[first + Math.Clamp(k, 0, last) * step];
            }

            for (var i = 0; i < count; i++)
            {
                target[first + i * step] = (byte)(total / window);
                total += source[first + Math.Min(i + radius + 1, last) * step]
                    - source[first + Math.Max(i - radius, 0) * step];
            }
        }
    }

    /// <summary>Enlarges the blurred picture to its size on screen, smoothly.</summary>
    private static byte[] Enlarge(byte[] work, int workWidth, int workHeight, int width, int height)
    {
        var pixels = new byte[width * height * BytesPerPixel];

        // Where each output column falls between two source columns, worked out once.
        var left = new int[width];
        var right = new int[width];
        var across = new double[width];
        for (var x = 0; x < width; x++)
        {
            var sx = Math.Clamp((x + 0.5) * workWidth / width - 0.5, 0, workWidth - 1);
            left[x] = (int)sx * BytesPerPixel;
            right[x] = Math.Min((int)sx + 1, workWidth - 1) * BytesPerPixel;
            across[x] = sx - (int)sx;
        }

        Parallel.For(0, height, y =>
        {
            var sy = Math.Clamp((y + 0.5) * workHeight / height - 0.5, 0, workHeight - 1);
            var top = (int)sy * workWidth * BytesPerPixel;
            var bottom = Math.Min((int)sy + 1, workHeight - 1) * workWidth * BytesPerPixel;
            var downward = sy - (int)sy;
            var row = y * width * BytesPerPixel;

            for (var x = 0; x < width; x++)
            {
                var a = across[x];
                var i = row + x * BytesPerPixel;

                for (var channel = 0; channel < 3; channel++)
                {
                    var upper = work[top + left[x] + channel] * (1 - a) + work[top + right[x] + channel] * a;
                    var lower = work[bottom + left[x] + channel] * (1 - a) + work[bottom + right[x] + channel] * a;
                    pixels[i + channel] = Round(upper * (1 - downward) + lower * downward);
                }
            }
        });

        return pixels;
    }

    private static double[] ShadeByRow(int height, double strength)
    {
        var alphas = new double[height];
        for (var y = 0; y < height; y++)
        {
            var offset = height > 1 ? (double)y / (height - 1) : 0;
            var stop = 1;
            while (stop < ShadeStops.Length - 1 && offset > ShadeStops[stop].Offset)
            {
                stop++;
            }

            var (fromOffset, fromAlpha) = ShadeStops[stop - 1];
            var (toOffset, toAlpha) = ShadeStops[stop];
            var t = Math.Clamp((offset - fromOffset) / (toOffset - fromOffset), 0, 1);
            alphas[y] = (fromAlpha + (toAlpha - fromAlpha) * t) * strength;
        }

        return alphas;
    }

    /// <summary>The same small offset for the same pixel every time, between -0.5 and 0.5.</summary>
    private static double Noise(int x, int y)
    {
        var hash = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        hash = (hash ^ (hash >> 13)) * 1274126177;
        return (hash >> 24) / 255.0 - 0.5;
    }

    private static byte Round(double value) => (byte)Math.Clamp(value + 0.5, 0, 255);

    private static BitmapSource Frozen(byte[] pixels, int width, int height)
    {
        // Opaque, so drawing it settled is a copy with no blending.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * BytesPerPixel);
        bitmap.Freeze();
        return bitmap;
    }
}
