using System.Globalization;
using System.Text;

namespace HandheldOptimiser.PowerService;

/// <summary>The numbers a Radeon setting can take, as the driver gives them.</summary>
internal readonly record struct RadeonRange(int Min, int Max, int Step)
{
    /// <summary><paramref name="value"/> brought inside the range and onto a step, which the driver refuses otherwise.</summary>
    public int Fit(int value)
    {
        var inside = Math.Clamp(value, Min, Math.Max(Min, Max));
        return Step > 1 ? Math.Min(Max, Min + ((inside - Min + (Step / 2)) / Step * Step)) : inside;
    }

    /// <summary>Min, Max and Step, as the status carries them.</summary>
    public int[] ToArray() => [Min, Max, Step];
}

/// <summary>
/// The AMD Radeon settings the widget offers, the same as in Radeon Software: Super Resolution and its sharpness,
/// Fluid Motion Frames, Anti-Lag, Chill and its lowest and highest frame rate, and Image Sharpening and its
/// strength. Each is null where this device does not have it or it could not be read.
///
/// The launcher reads and sets them through AMD's ADLX in the player's session, as ADLX crashes when asked
/// about them from the service's session (tried on an Ally on 2026-10-28), and prints what it found as one
/// line (<see cref="Format"/>), which TDP Engine reads (<see cref="Parse"/>). Shared with the launcher as a
/// linked file.
/// </summary>
internal sealed record RadeonState
{
    /// <summary>
    /// The launcher's argument: alone it reads; followed by a setting's name and on, off or a number it sets that
    /// setting first.
    /// </summary>
    public const string Argument = "--radeon";

    // The settings switched on and off.
    public const string RsrName = "rsr";
    public const string AfmfName = "afmf";
    public const string AntiLagName = "anti-lag";
    public const string ChillName = "chill";
    public const string SharpeningName = "sharpening";

    // The settings that take a number.
    public const string RsrSharpnessName = "rsr-sharpness";
    public const string ChillMinName = "chill-min";
    public const string ChillMaxName = "chill-max";
    public const string SharpeningStrengthName = "sharpening-strength";

    public const string OnName = "on";
    public const string OffName = "off";

    // Where a range is written in the line: the number's name with this after it.
    private const string RangeSuffix = "-range";

    /// <summary>The names of the settings switched on and off.</summary>
    public static readonly string[] Switches = [RsrName, AfmfName, AntiLagName, ChillName, SharpeningName];

    /// <summary>The names of the settings that take a number.</summary>
    public static readonly string[] Numbers = [RsrSharpnessName, ChillMinName, ChillMaxName, SharpeningStrengthName];

    public bool? Rsr { get; init; }
    public int? RsrSharpness { get; init; }
    public RadeonRange? RsrSharpnessRange { get; init; }
    public bool? Afmf { get; init; }
    public bool? AntiLag { get; init; }
    public bool? Chill { get; init; }
    public int? ChillMin { get; init; }
    public int? ChillMax { get; init; }

    /// <summary>The frame rates Chill's lowest and highest can be set to. One range serves both.</summary>
    public RadeonRange? ChillRange { get; init; }

    public bool? Sharpening { get; init; }
    public int? SharpeningStrength { get; init; }
    public RadeonRange? SharpeningRange { get; init; }

    /// <summary>Nothing known: before the first read, or where ADLX could not be used.</summary>
    public static RadeonState Unknown { get; } = new();

    /// <summary>The line the launcher prints: name=value pairs split by spaces, with anything not known left out.</summary>
    public string Format()
    {
        var line = new StringBuilder();
        Add(line, RsrName, Rsr);
        Add(line, RsrSharpnessName, RsrSharpness);
        Add(line, RsrSharpnessName + RangeSuffix, RsrSharpnessRange);
        Add(line, AfmfName, Afmf);
        Add(line, AntiLagName, AntiLag);
        Add(line, ChillName, Chill);
        Add(line, ChillMinName, ChillMin);
        Add(line, ChillMaxName, ChillMax);
        Add(line, ChillName + RangeSuffix, ChillRange);
        Add(line, SharpeningName, Sharpening);
        Add(line, SharpeningStrengthName, SharpeningStrength);
        Add(line, SharpeningStrengthName + RangeSuffix, SharpeningRange);
        return line.ToString();
    }

    /// <summary>
    /// The state a launcher's line says, or null when there is no output at all. An empty line is a driver that
    /// offers none of them. A pair this does not know, as from a newer launcher, is passed over.
    /// </summary>
    public static RadeonState? Parse(string? line)
    {
        if (line is null)
        {
            return null;
        }

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (pair.Split('=', 2) is [var name, var value])
            {
                pairs[name] = value;
            }
        }

        return new RadeonState
        {
            Rsr = Flag(pairs, RsrName),
            RsrSharpness = Number(pairs, RsrSharpnessName),
            RsrSharpnessRange = Range(pairs, RsrSharpnessName + RangeSuffix),
            Afmf = Flag(pairs, AfmfName),
            AntiLag = Flag(pairs, AntiLagName),
            Chill = Flag(pairs, ChillName),
            ChillMin = Number(pairs, ChillMinName),
            ChillMax = Number(pairs, ChillMaxName),
            ChillRange = Range(pairs, ChillName + RangeSuffix),
            Sharpening = Flag(pairs, SharpeningName),
            SharpeningStrength = Number(pairs, SharpeningStrengthName),
            SharpeningRange = Range(pairs, SharpeningStrengthName + RangeSuffix),
        };
    }

    /// <summary>Whether <paramref name="name"/> with <paramref name="on"/> or <paramref name="value"/> is a change this knows.</summary>
    public static bool IsChange(string? name, bool? on, int? value) =>
        (on is not null && Switches.Contains(name)) || (value is not null && Numbers.Contains(name));

    private static void Add(StringBuilder line, string name, bool? on) =>
        Add(line, name, on is { } value ? (value ? "1" : "0") : null);

    private static void Add(StringBuilder line, string name, int? number) =>
        Add(line, name, number?.ToString(CultureInfo.InvariantCulture));

    private static void Add(StringBuilder line, string name, RadeonRange? range) =>
        Add(line, name, range is { } r ? string.Join(',', r.ToArray().Select(n => n.ToString(CultureInfo.InvariantCulture))) : null);

    private static void Add(StringBuilder line, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (line.Length > 0)
        {
            line.Append(' ');
        }

        line.Append(name).Append('=').Append(value);
    }

    private static bool? Flag(Dictionary<string, string> pairs, string name) =>
        pairs.TryGetValue(name, out var value) ? value switch { "1" => true, "0" => false, _ => null } : null;

    private static int? Number(Dictionary<string, string> pairs, string name) =>
        pairs.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static RadeonRange? Range(Dictionary<string, string> pairs, string name) =>
        pairs.TryGetValue(name, out var value)
        && value.Split(',') is [var min, var max, var step]
        && int.TryParse(min, NumberStyles.Integer, CultureInfo.InvariantCulture, out var low)
        && int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var high)
        && int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by)
        && low <= high
            ? new RadeonRange(low, high, by)
            : null;
}
