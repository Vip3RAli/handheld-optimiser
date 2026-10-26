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
internal sealed record StatTile(string Label, string Value, string Unit, StatTone Tone = StatTone.Normal);

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

    // Shown for a reading that is not known.
    private const string Unknown = "–";

    // The processor's temperature from which its tile turns amber, then red. The Ally slows itself at 95 °C.
    private const int WarmDegrees = 80;
    private const int HotDegrees = 90;

    // The battery's charge at or under which its tile turns amber, then red, while not charging.
    private const int LowBattery = 25;
    private const int VeryLowBattery = 15;

    private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

    /// <summary>
    /// The six tiles, in order: frame rate, power limit, temperature, processor use, graphics use and battery.
    /// They are always all there, so nothing moves about; a reading not known shows a dash.
    /// </summary>
    public static IReadOnlyList<StatTile> Tiles(OverlayReading reading) =>
    [
        new("FPS", reading.Fps is { } fps ? Math.Round(fps).ToString(Numbers) : Unknown, string.Empty),
        new("TDP", reading.Watts is { } watts ? watts.ToString(Numbers) : Unknown, reading.Watts is null ? string.Empty : "W"),
        new("TEMP", reading.Temperature is { } degrees ? degrees.ToString(Numbers) : Unknown,
            reading.Temperature is null ? string.Empty : "°C", TemperatureTone(reading.Temperature)),
        Percent("CPU", reading.Cpu),
        Percent("GPU", reading.Gpu),
        new(reading.Charging ? "CHARGING" : "BATTERY", reading.Battery is { } charge ? charge.ToString(Numbers) : Unknown,
            reading.Battery is null ? string.Empty : "%", BatteryTone(reading.Battery, reading.Charging))
    ];

    /// <summary>The time as the player's own settings write it, without seconds.</summary>
    public static string Clock(DateTime now, CultureInfo culture) => now.ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    /// <summary>The day and date under the time, such as "Sunday 25 October".</summary>
    public static string Date(DateTime now, CultureInfo culture) => now.ToString("dddd d MMMM", culture);

    public static StatTone TemperatureTone(int? degrees) => degrees switch
    {
        >= HotDegrees => StatTone.Hot,
        >= WarmDegrees => StatTone.Warm,
        _ => StatTone.Normal
    };

    public static StatTone BatteryTone(int? charge, bool charging) => charging || charge is not { } percent
        ? StatTone.Normal
        : percent <= VeryLowBattery ? StatTone.Hot
        : percent <= LowBattery ? StatTone.Warm
        : StatTone.Normal;

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
        ? new StatTile(label, Math.Clamp(Math.Round(percent), 0, 100).ToString(Numbers), "%")
        : new StatTile(label, Unknown, string.Empty);
}
