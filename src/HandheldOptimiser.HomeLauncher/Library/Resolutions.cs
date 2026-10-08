namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A screen size in pixels, written as Windows shows it: 1920 x 1080.</summary>
internal readonly record struct Resolution(int Width, int Height)
{
    public override string ToString() => $"{Width} x {Height}";

    /// <summary>"1280x720", as game profiles store it.</summary>
    public string Key => $"{Width}x{Height}";

    public static Resolution? Parse(string text) =>
        text.Split('x', 'X') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height)
            && width > 0 && height > 0
            ? new Resolution(width, height)
            : null;
}

/// <summary>
/// The main screen's resolution. A lower one, such as 720p on the Ally, takes load off the graphics and
/// so stretches the battery, with the screen scaling the picture up. Changed for this user only, which
/// needs no administrator rights, and kept after a restart as Windows' own display settings do.
/// </summary>
internal static class Resolutions
{
    // Smaller than this is too small to read the library or most games on a handheld.
    private const int MinimumHeight = 600;

    /// <summary>The current resolution and the ones worth offering, largest first.</summary>
    public static (Resolution Current, IReadOnlyList<Resolution> Available)? Read()
    {
        if (!Native.CurrentDisplayMode(out var current))
        {
            return null;
        }

        var modes = new List<(Resolution Size, uint Bits, bool Interlaced)>();
        for (var i = 0; Native.DisplayMode(i, out var mode); i++)
        {
            modes.Add((new Resolution((int)mode.PelsWidth, (int)mode.PelsHeight), mode.BitsPerPel,
                (mode.DisplayFlags & Native.DmInterlaced) != 0));
        }

        var now = new Resolution((int)current.PelsWidth, (int)current.PelsHeight);
        return (now, Choices(modes.Where(m => m.Bits == current.BitsPerPel && !m.Interlaced).Select(m => m.Size), now));
    }

    /// <summary>
    /// The sizes to step through: those the shape of the screen's largest, so the picture is not
    /// stretched, and never smaller than <see cref="MinimumHeight"/>. The current one is always among them.
    /// </summary>
    public static List<Resolution> Choices(IEnumerable<Resolution> offered, Resolution current)
    {
        var sizes = offered.Where(r => r.Height >= MinimumHeight).Append(current).Distinct().ToList();
        var largest = sizes.MaxBy(r => (long)r.Width * r.Height);
        var shape = (double)largest.Width / largest.Height;

        // A screen with no other size of the same shape gets all of them.
        var sameShape = sizes.Where(r => Math.Abs((double)r.Width / r.Height - shape) < 0.02 || r == current).ToList();
        return (sameShape.Count > 1 ? sameShape : sizes)
            .OrderByDescending(r => r.Width).ThenByDescending(r => r.Height)
            .ToList();
    }

    /// <returns>An error message, or null when the screen switched.</returns>
    /// <param name="hertz">The refresh rate to keep, when the new size offers it; otherwise its fastest.</param>
    public static string? Set(Resolution size, int? hertz = null)
    {
        if (!Native.CurrentDisplayMode(out var mode))
        {
            return "The screen's settings could not be read.";
        }

        var wanted = hertz ?? (int)mode.DisplayFrequency;
        if (mode.PelsWidth == size.Width && mode.PelsHeight == size.Height && mode.DisplayFrequency == wanted)
        {
            return null;
        }

        var rates = new SortedSet<int>();
        for (var i = 0; Native.DisplayMode(i, out var offered); i++)
        {
            if (offered.PelsWidth == size.Width && offered.PelsHeight == size.Height && offered.BitsPerPel == mode.BitsPerPel
                && (offered.DisplayFlags & Native.DmInterlaced) == 0 && offered.DisplayFrequency > 1)
            {
                rates.Add((int)offered.DisplayFrequency);
            }
        }

        if (rates.Count == 0)
        {
            return $"The screen does not offer {size}.";
        }

        mode.PelsWidth = (uint)size.Width;
        mode.PelsHeight = (uint)size.Height;
        mode.DisplayFrequency = (uint)(rates.Contains(wanted) ? wanted : rates.Max);
        mode.Fields = Native.DmPelsWidth | Native.DmPelsHeight | Native.DmDisplayFrequency;

        return Native.ChangeDisplayMode(ref mode) switch
        {
            Native.DispChangeSuccessful => null,
            Native.DispChangeRestart => "Windows needs a restart to change the resolution.",
            var code => $"The screen did not accept {size} (error {code})."
        };
    }
}
