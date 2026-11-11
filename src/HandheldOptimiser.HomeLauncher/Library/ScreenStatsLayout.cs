using System.Globalization;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>How a tile's value reads against what is safe: as normal, getting warm, or a problem.</summary>
internal enum StatTone
{
    Normal,
    Warm,
    Hot
}

/// <summary>One tile of the stats screen: a short name, a big value, and its unit beside it.</summary>
/// <param name="Fraction">How far along the line under the value is filled, 0 to 1, or null for no line.</param>
/// <param name="Caption">A few small words at the tile's top right, such as the frame rate aimed for, or none.</param>
internal sealed record StatTile(string Label, string Value, string Unit, StatTone Tone = StatTone.Normal,
    double? Fraction = null, string Caption = "");

/// <summary>What the badges at the stats screen's top right show.</summary>
/// <param name="Battery">The charge, 0 to 100, or null with no battery.</param>
/// <param name="Charging">On mains power.</param>
/// <param name="BatteryCare">Whether charging stops at 80%.</param>
/// <param name="WifiQuality">0 to 100 when connected, 0 when not, null with no Wi-Fi adapter.</param>
/// <param name="WifiName">The network's name, or null when not connected.</param>
internal sealed record StatsHeader(int? Battery = null, bool Charging = false, bool BatteryCare = false,
    int? WifiQuality = null, string? WifiName = null)
{
    /// <summary>Wi-Fi quality as 0 to 4 bars, as the library's top bar counts them.</summary>
    public int WifiBars => new SystemStatus(null, false, WifiQuality).WifiBars;

    /// <summary>The words beside the bars: the network's name, or that there is none.</summary>
    public string WifiText => WifiQuality is > 0 ? string.IsNullOrWhiteSpace(WifiName) ? "WI-FI" : WifiName : "OFFLINE";

    /// <summary>How much of the battery's picture is filled, 0 to 1.</summary>
    public double BatteryFraction => Math.Clamp((Battery ?? 0) / 100.0, 0, 1);

    /// <summary>The charge has run low, which only matters while it is not charging.</summary>
    public bool BatteryLow => !Charging && Battery is < ScreenStatsLayout.LowBattery;
}

/// <summary>A rectangle of the Windows desktop, in pixels.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>Whether the two share any pixel.</summary>
    public bool Overlaps(ScreenRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>Whether this one covers all of the other.</summary>
    public bool Covers(ScreenRect other) => X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>
/// What the stats screen shows, and where the Ally's panel goes beside the monitor. No window and no Windows
/// calls, so it can be tested.
/// </summary>
internal static class ScreenStatsLayout
{
    /// <summary>The panel's size for the stats screen: 720p, as big text needs no more.</summary>
    public const int PanelWidth = 1280;
    public const int PanelHeight = 720;

    /// <summary>The charge below which the battery's badge turns red.</summary>
    public const int LowBattery = 20;

    // What each line under a value is measured against. The Ally's highest power limit is 30 W, it slows itself
    // at 95 °C, and its processor reaches about 5 GHz.
    private const double MostWatts = 30;
    private const double MostDegrees = 95;
    private const double MostGhz = 5;

    // Shown for a reading that is not known.
    private const string Unknown = "–";

    // The processor's temperature from which its tile turns amber, then red. The Ally slows itself at 95 °C.
    private const int WarmDegrees = 80;
    private const int HotDegrees = 90;

    private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

    /// <summary>
    /// The six tiles, in order: frame rate, power, temperature, processor use, processor clock and graphics use.
    /// No battery: that is a badge in the header. They are always all there, so nothing moves about; a reading
    /// not known shows a dash, and has no line under it.
    /// </summary>
    /// <param name="targetFps">The frame rate TDP Engine aims for, or 0 for none.</param>
    public static IReadOnlyList<StatTile> Tiles(OverlayReading reading, int targetFps = 0) =>
    [
        Fps(reading.Fps, targetFps),
        Power(reading),
        new("TEMP", reading.Temperature is { } degrees ? degrees.ToString(Numbers) : Unknown,
            reading.Temperature is null ? string.Empty : "°C", TemperatureTone(reading.Temperature),
            Part(reading.Temperature, MostDegrees)),
        Percent("CPU", reading.Cpu),
        reading.CpuGhz is { } ghz
            ? new("CPU CLOCK", ghz.ToString("0.0", Numbers), "GHz", Fraction: Part(ghz, MostGhz))
            : new("CPU CLOCK", Unknown, string.Empty),
        Percent("GPU", reading.Gpu)
    ];

    /// <summary>
    /// The time's digits as the player's own settings write them, without seconds and without am or pm, which
    /// <see cref="ClockPeriod"/> gives to be set small beside them.
    /// </summary>
    public static string ClockTime(DateTime now, CultureInfo culture) =>
        now.ToString(culture.DateTimeFormat.ShortTimePattern.Replace("tt", string.Empty).Replace("t", string.Empty).Trim(), culture).Trim();

    /// <summary>"am" or "pm" where the player's settings write the time with one, otherwise nothing.</summary>
    public static string ClockPeriod(DateTime now, CultureInfo culture) =>
        culture.DateTimeFormat.ShortTimePattern.Contains('t') ? now.ToString("tt", culture).ToLower(culture) : string.Empty;

    /// <summary>The day and date under the time, in capitals, such as "SUNDAY 25 OCTOBER".</summary>
    public static string Date(DateTime now, CultureInfo culture) => now.ToString("dddd d MMMM", culture).ToUpper(culture);

    public static StatTone TemperatureTone(int? degrees) => degrees switch
    {
        >= HotDegrees => StatTone.Hot,
        >= WarmDegrees => StatTone.Warm,
        _ => StatTone.Normal
    };

    /// <summary>
    /// The limit TDP Engine holds, as TDP. On Auto it holds none, and the firmware does not say the limit it
    /// uses, so the power drawn now is shown instead, named POWER so it is not taken for a limit.
    /// </summary>
    private static StatTile Power(OverlayReading reading) => reading switch
    {
        { Watts: { } watts } => new("TDP", watts.ToString(Numbers), "W", Fraction: Part(watts, MostWatts)),
        { PowerDrawn: { } drawn } => new("POWER", Math.Round(drawn).ToString(Numbers), "W", Fraction: Part(drawn, MostWatts)),
        _ => new("TDP", Unknown, string.Empty)
    };

    /// <summary>
    /// The frame rate. With a frame rate aimed for, the tile names it and its line fills as the game nears it;
    /// with none there is nothing to measure against, so no line.
    /// </summary>
    private static StatTile Fps(double? fps, int targetFps)
    {
        var caption = targetFps > 0 ? "TARGET " + targetFps.ToString(Numbers) : string.Empty;
        return fps is { } frames
            ? new("FPS", Math.Round(frames).ToString(Numbers), string.Empty, Fraction: targetFps > 0 ? Part(frames, targetFps) : null, Caption: caption)
            : new("FPS", Unknown, string.Empty, Caption: caption);
    }

    /// <summary>A reading as a part of the most it can be, 0 to 1, or null when it is not known.</summary>
    private static double? Part(double? value, double most) => value is { } known ? Math.Clamp(known / most, 0, 1) : null;

    /// <summary>
    /// The panel's size: 720p when it offers that, otherwise the size it has now, as a size the panel does
    /// not list may be refused.
    /// </summary>
    /// <param name="offered">Every width and height the panel lists.</param>
    public static (int Width, int Height) PanelSize(IEnumerable<(int Width, int Height)> offered, int currentWidth, int currentHeight) =>
        offered.Contains((PanelWidth, PanelHeight)) ? (PanelWidth, PanelHeight) : (currentWidth, currentHeight);

    /// <summary>
    /// Where each monitor and the panel go once the panel is turned on beside them. The main monitor (the one at
    /// the desktop's corner, or else the first) is put at the corner, which keeps it the main screen, so games
    /// and the library open there; the others keep their places around it. The panel goes under them all,
    /// centred under the main monitor, as the Ally sits under it on the desk.
    /// </summary>
    public static (IReadOnlyList<ScreenRect> Monitors, ScreenRect Panel) Arrange(IReadOnlyList<ScreenRect> monitors,
        int panelWidth, int panelHeight)
    {
        if (monitors.Count == 0)
        {
            return (monitors, new ScreenRect(0, 0, panelWidth, panelHeight));
        }

        var main = MainMonitor(monitors);
        var moved = monitors.Select(m => m with { X = m.X - main.X, Y = m.Y - main.Y }).ToList();
        var bottom = moved.Max(m => m.Bottom);
        return (moved, new ScreenRect((main.Width - panelWidth) / 2, bottom, panelWidth, panelHeight));
    }

    /// <summary>Whether a window is on the panel, which is where its middle is.</summary>
    public static bool IsOn(ScreenRect window, ScreenRect panel)
    {
        var x = window.X + window.Width / 2;
        var y = window.Y + window.Height / 2;
        return x >= panel.X && x < panel.Right && y >= panel.Y && y < panel.Bottom;
    }

    /// <summary>
    /// Where a window on the panel goes on the monitor: the same distance from the corner as it was on the
    /// panel, made smaller only when it is bigger than the monitor's free area, and kept inside it.
    /// </summary>
    /// <param name="area">The monitor's free area, without its taskbar.</param>
    public static ScreenRect MoveOff(ScreenRect window, ScreenRect panel, ScreenRect area)
    {
        var width = Math.Min(window.Width, area.Width);
        var height = Math.Min(window.Height, area.Height);
        var x = Math.Clamp(area.X + window.X - panel.X, area.X, area.Right - width);
        var y = Math.Clamp(area.Y + window.Y - panel.Y, area.Y, area.Bottom - height);
        return new ScreenRect(x, y, width, height);
    }

    /// <summary>
    /// Where a window that fills one area goes to fill another, as a maximised or full screen window does.
    /// It keeps how far it reaches past each edge, as a maximised window's border does.
    /// </summary>
    public static ScreenRect Fill(ScreenRect window, ScreenRect from, ScreenRect to) => new(
        to.X - (from.X - window.X),
        to.Y - (from.Y - window.Y),
        to.Width + window.Width - from.Width,
        to.Height + window.Height - from.Height);

    /// <summary>
    /// Where the mouse is kept: around every monitor, which leaves out the panel as it sits under them all.
    /// When that would take in the panel, as when Windows would not move it under, only the main monitor.
    /// </summary>
    /// <returns>The area, or null when there is no monitor.</returns>
    public static ScreenRect? CursorArea(IReadOnlyList<ScreenRect> monitors, ScreenRect panel)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var left = monitors.Min(m => m.X);
        var top = monitors.Min(m => m.Y);
        var all = new ScreenRect(left, top, monitors.Max(m => m.Right) - left, monitors.Max(m => m.Bottom) - top);
        return all.Overlaps(panel) ? MainMonitor(monitors) : all;
    }

    /// <summary>The main monitor: the one at the desktop's corner, or else the first.</summary>
    public static ScreenRect MainMonitor(IReadOnlyList<ScreenRect> monitors) =>
        monitors.FirstOrDefault(m => m is { X: 0, Y: 0 }, monitors[0]);

    private static StatTile Percent(string label, double? value) => value is { } percent
        ? new StatTile(label, Math.Clamp(Math.Round(percent), 0, 100).ToString(Numbers), "%", Fraction: Part(percent, 100))
        : new StatTile(label, Unknown, string.Empty);
}
