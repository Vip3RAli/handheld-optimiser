namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The main screen's refresh rate, such as 60 or 120 Hz on the Ally. Changed for this user only, which
/// needs no administrator rights, and kept after a restart as Windows' own display settings do.
/// </summary>
internal static class RefreshRates
{
    /// <summary>The current rate and every rate the screen offers at its current resolution, lowest first.</summary>
    public static (int Current, IReadOnlyList<int> Available)? Read()
    {
        if (!Native.CurrentDisplayMode(out var current))
        {
            return null;
        }

        var rates = new SortedSet<int>();
        for (var i = 0; Native.DisplayMode(i, out var mode); i++)
        {
            if (mode.PelsWidth == current.PelsWidth && mode.PelsHeight == current.PelsHeight
                && mode.BitsPerPel == current.BitsPerPel && (mode.DisplayFlags & Native.DmInterlaced) == 0
                && mode.DisplayFrequency > 1)
            {
                rates.Add((int)mode.DisplayFrequency);
            }
        }

        // 0 and 1 mean the hardware default, with no number to show.
        if (current.DisplayFrequency <= 1)
        {
            return null;
        }

        rates.Add((int)current.DisplayFrequency);
        return ((int)current.DisplayFrequency, rates.ToList());
    }

    /// <returns>An error message, or null when the screen switched.</returns>
    public static string? Set(int hertz)
    {
        if (!Native.CurrentDisplayMode(out var mode))
        {
            return "The screen's settings could not be read.";
        }

        if (mode.DisplayFrequency == hertz)
        {
            return null;
        }

        mode.DisplayFrequency = (uint)hertz;
        mode.Fields = Native.DmDisplayFrequency;

        return Native.ChangeDisplayMode(ref mode) switch
        {
            Native.DispChangeSuccessful => null,
            Native.DispChangeRestart => "Windows needs a restart to change the refresh rate.",
            var code => $"The screen did not accept {hertz} Hz (error {code})."
        };
    }
}
