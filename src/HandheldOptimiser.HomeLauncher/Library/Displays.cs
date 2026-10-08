namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Whether the library is on a TV or monitor rather than the handheld's own screen, which is what docked
/// mode follows. Windows says how each screen is connected: the built-in panel is wired inside the device,
/// and anything on HDMI, DisplayPort or USB-C is plugged in.
/// </summary>
internal static class Displays
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values for a panel built into the device.
    private const uint Lvds = 6;
    private const uint DisplayPortEmbedded = 11;
    private const uint UdiEmbedded = 13;
    private const uint Internal = 0x80000000;

    // The width, in device-independent pixels, the library is laid out for on the Ally: 1920 pixels at 150%.
    private const double HandheldWidth = 1280;

    public static bool IsBuiltIn(uint technology) => technology is Lvds or DisplayPortEmbedded or UdiEmbedded or Internal;

    /// <summary>Whether the window's screen shows on a TV or monitor, including one it is duplicated to.</summary>
    public static bool IsDocked(nint window) =>
        Native.ScreenOf(window) is { } screen && IsDocked(Native.ScreenConnections(), screen);

    /// <param name="connections">Every screen Windows draws, with how each place it shows on is connected.</param>
    /// <param name="screen">The screen the library is on.</param>
    public static bool IsDocked(IEnumerable<(string Screen, uint Technology)> connections, string screen) =>
        connections.Any(c => string.Equals(c.Screen, screen, StringComparison.OrdinalIgnoreCase) && !IsBuiltIn(c.Technology));

    /// <summary>
    /// How much bigger to draw the library on a TV or monitor. A size of 0 fits it to the screen: as many
    /// games across as on the handheld, however wide the screen is in Windows' own units.
    /// </summary>
    /// <param name="size">The player's choice, as a percentage, or 0 to fit the screen.</param>
    /// <param name="width">The window's width in device-independent pixels.</param>
    public static double Scale(int size, double width) =>
        size > 0 ? size / 100.0 : Math.Clamp(width / HandheldWidth, 1, 3);
}
