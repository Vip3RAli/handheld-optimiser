using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Picks the two colours that stand out in a game's cover or icon, for the gradient shown behind the
/// grid while that game has the focus.
/// </summary>
internal static class Palette
{
    // The image is shrunk to about this many pixels a side first: plenty to find its main colours.
    private const int SampleSize = 32;

    private const int HueBins = 12;
    private const byte MinAlpha = 128;

    // Pixels duller or darker than this say little about what colour the artwork "is".
    private const double MinSaturation = 0.25;
    private const double MinValue = 0.2;

    // The second colour must be a different hue, and common enough not to be a stray detail.
    private const int MinBinsApart = 2;
    private const double MinSecondShare = 0.12;

    /// <returns>A frozen diagonal gradient, or null when the image cannot be read.</returns>
    public static Brush? Gradient(ImageSource? image)
    {
        if (image is not BitmapSource bitmap || Pixels(bitmap) is not { } pixels)
        {
            return null;
        }

        var weight = new double[HueBins];
        var sums = new (double R, double G, double B)[HueBins];
        (double R, double G, double B) all = default;
        var counted = 0;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < MinAlpha)
            {
                continue;
            }

            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            all = (all.R + r, all.G + g, all.B + b);
            counted++;

            var (hue, saturation, value) = ToHsv(r, g, b);
            if (saturation < MinSaturation || value < MinValue)
            {
                continue;
            }

            // Vivid pixels count for more, so a splash of colour beats a wash of near grey.
            var bin = (int)(hue / 360 * HueBins) % HueBins;
            var w = saturation * value;
            weight[bin] += w;
            sums[bin] = (sums[bin].R + r * w, sums[bin].G + g * w, sums[bin].B + b * w);
        }

        if (counted == 0)
        {
            return null;
        }

        var first = Array.IndexOf(weight, weight.Max());
        if (weight[first] <= 0)
        {
            // Black and white artwork: a gradient of its overall tone.
            var tone = Color.FromRgb((byte)(all.R / counted), (byte)(all.G / counted), (byte)(all.B / counted));
            return Diagonal(Shade(tone, 1.25), Shade(tone, 0.6));
        }

        var second = -1;
        for (var bin = 0; bin < HueBins; bin++)
        {
            var apart = Math.Min(Math.Abs(bin - first), HueBins - Math.Abs(bin - first));
            if (apart >= MinBinsApart && weight[bin] >= weight[first] * MinSecondShare
                && (second < 0 || weight[bin] > weight[second]))
            {
                second = bin;
            }
        }

        var primary = Lift(Average(sums[first], weight[first]));

        // Artwork that is all one hue fades into a darker shade of it.
        return Diagonal(primary, second < 0 ? Shade(primary, 0.45) : Lift(Average(sums[second], weight[second])));
    }

    private static byte[]? Pixels(BitmapSource bitmap)
    {
        try
        {
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            {
                return null;
            }

            var scale = Math.Min(1.0, (double)SampleSize / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);

            var stride = small.PixelWidth * 4;
            var pixels = new byte[stride * small.PixelHeight];
            small.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
            or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    private static Color Average((double R, double G, double B) sum, double weight) =>
        Color.FromRgb((byte)(sum.R / weight), (byte)(sum.G / weight), (byte)(sum.B / weight));

    /// <summary>Brings a colour up to a brightness that still reads as a colour under the dark shade.</summary>
    private static Color Lift(Color colour)
    {
        var peak = Math.Max(colour.R, Math.Max(colour.G, colour.B));
        return peak is 0 or >= 200 ? colour : Shade(colour, 200.0 / peak);
    }

    private static Color Shade(Color colour, double factor) => Color.FromRgb(
        (byte)Math.Clamp(colour.R * factor, 0, 255),
        (byte)Math.Clamp(colour.G * factor, 0, 255),
        (byte)Math.Clamp(colour.B * factor, 0, 255));

    private static Brush Diagonal(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static (double Hue, double Saturation, double Value) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (max == 0 || delta == 0)
        {
            return (0, 0, max / 255);
        }

        var hue = max == r ? (g - b) / delta % 6
            : max == g ? (b - r) / delta + 2
            : (r - g) / delta + 4;

        return ((hue * 60 + 360) % 360, delta / max, max / 255);
    }
}
