using System.Globalization;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One second's readings for the performance overlay. Anything not known is null.</summary>
/// <param name="Fps">The frame rate of the program in front, from TDP Engine.</param>
/// <param name="Watts">The power limit TDP Engine holds, or null when it holds none.</param>
/// <param name="Temperature">The processor's temperature in °C, from TDP Engine.</param>
/// <param name="Cpu">Processor use, 0 to 100.</param>
/// <param name="Gpu">Graphics use, 0 to 100.</param>
/// <param name="MemoryUsedGb">Memory in use, in GB.</param>
/// <param name="MemoryTotalGb">All the memory there is, in GB.</param>
/// <param name="Battery">The battery's charge, 0 to 100, or null with no battery.</param>
/// <param name="Charging">Whether the charger is plugged in.</param>
/// <param name="CpuGhz">The processor's clock now, in GHz, as Task Manager's speed figure.</param>
/// <param name="PowerDrawn">The power the processor and graphics draw now, in watts, from Windows' energy meter.</param>
internal sealed record OverlayReading(
    double? Fps = null,
    int? Watts = null,
    int? Temperature = null,
    double? Cpu = null,
    double? Gpu = null,
    double? MemoryUsedGb = null,
    double? MemoryTotalGb = null,
    int? Battery = null,
    bool Charging = false,
    double? CpuGhz = null,
    double? PowerDrawn = null);

/// <summary>One row of the overlay: a short name and its value.</summary>
internal sealed record OverlayLine(string Label, string Value);

/// <summary>What the overlay shows at each level, and where it sits. No window, so it can be tested.</summary>
internal static class OverlayLayout
{
    /// <summary>The gap between the overlay and the edges of the screen, in device independent pixels.</summary>
    public const double Margin = 12;

    private static readonly CultureInfo Text = CultureInfo.InvariantCulture;

    /// <summary>
    /// The rows for a level. Small is the frame rate alone. Medium adds the power limit (only while TDP
    /// Engine holds one), processor and graphics use, and the temperature. Full adds the frame time, memory
    /// and battery. A reading that is not known shows as a question mark, apart from the power limit and
    /// the temperature, which are left out, as most devices or moments have none.
    /// </summary>
    public static IReadOnlyList<OverlayLine> Lines(string level, OverlayReading reading)
    {
        var lines = new List<OverlayLine> { new("FPS", reading.Fps is { } fps ? Math.Round(fps).ToString(Text) : "?") };
        if (level == AutoTdpProtocol.OverlaySmall)
        {
            return lines;
        }

        if (reading.Watts is { } watts)
        {
            lines.Add(new OverlayLine("TDP", $"{watts} W"));
        }

        lines.Add(new OverlayLine("CPU", Percent(reading.Cpu)));
        lines.Add(new OverlayLine("GPU", Percent(reading.Gpu)));

        if (reading.Temperature is { } degrees)
        {
            lines.Add(new OverlayLine("Temp", $"{degrees} °C"));
        }

        if (level != AutoTdpProtocol.OverlayFull)
        {
            return lines;
        }

        lines.Add(new OverlayLine("Frame", reading.Fps is { } rate && rate > 0.5 ? $"{(1000 / rate).ToString("0.0", Text)} ms" : "?"));
        lines.Add(new OverlayLine("RAM", reading is { MemoryUsedGb: { } used, MemoryTotalGb: { } total }
            ? $"{used.ToString("0.0", Text)} / {total.ToString("0", Text)} GB"
            : "?"));

        if (reading.Battery is { } battery)
        {
            lines.Add(new OverlayLine("Battery", $"{battery}%{BatteryNote(reading)}"));
        }

        return lines;
    }

    /// <summary>Where the overlay's top left goes, for its corner, size and the screen's size.</summary>
    public static (double Left, double Top) Place(string corner, double screenWidth, double screenHeight, double width, double height)
    {
        var right = corner is AutoTdpProtocol.TopRight or AutoTdpProtocol.BottomRight;
        var bottom = corner is AutoTdpProtocol.BottomLeft or AutoTdpProtocol.BottomRight;
        return (
            right ? Math.Max(0, screenWidth - width - Margin) : Margin,
            bottom ? Math.Max(0, screenHeight - height - Margin) : Margin);
    }

    private static string Percent(double? value) =>
        value is { } percent ? $"{Math.Clamp(Math.Round(percent), 0, 100).ToString(Text)}%" : "?";

    // The charge alone: Windows' guess at the time left jumps about in a game, and the user had it taken out.
    private static string BatteryNote(OverlayReading reading) => reading.Charging ? ", charging" : string.Empty;
}
