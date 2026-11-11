namespace HandheldOptimiser.PowerService;

/// <summary>How the Ally's controller behaves. The number is what the controller is sent.</summary>
internal enum ControllerMode
{
    /// <summary>Sticks and buttons act as a game controller.</summary>
    Gamepad = 1,

    /// <summary>Sticks and buttons act as a mouse and keyboard.</summary>
    Desktop = 3,
}

/// <summary>The reports sent to the Ally's controller, and how long to wait between looks for it.</summary>
internal static class AllyCommands
{
    /// <summary>The report ID every command and every button report starts with.</summary>
    public const byte ReportId = 0x5A;

    /// <summary>The length every report is sent at, with unused bytes zero.</summary>
    public const int FeatureLength = 64;

    /// <summary>The report that wakes the controller: the ID, then "ASUS Tech.Inc." in ASCII.</summary>
    public static byte[] Wake()
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        "ASUS Tech.Inc."u8.CopyTo(report.AsSpan(1));
        return report;
    }

    /// <summary>The report that sets the controller's mode.</summary>
    public static byte[] Mode(ControllerMode mode)
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = 0xD1;
        report[2] = 0x01;
        report[3] = 0x01;
        report[4] = (byte)mode;
        return report;
    }

    /// <summary>The mode a name stands for, or null when it is not one.</summary>
    public static ControllerMode? ParseMode(string? name)
    {
        if (string.Equals(name, AutoTdpProtocol.GamepadMode, StringComparison.OrdinalIgnoreCase))
        {
            return ControllerMode.Gamepad;
        }

        return string.Equals(name, AutoTdpProtocol.DesktopMode, StringComparison.OrdinalIgnoreCase) ? ControllerMode.Desktop : null;
    }

    /// <summary>The lower case name of a mode, as a request carries it.</summary>
    public static string ModeName(ControllerMode mode) => mode == ControllerMode.Desktop
        ? AutoTdpProtocol.DesktopMode
        : AutoTdpProtocol.GamepadMode;

    /// <summary>
    /// The mode the controller should be in: a hold, while Game Bar is open, wins over the mode the player
    /// saved, which comes back once the hold is released. Null when neither is set.
    /// </summary>
    public static ControllerMode? ModeToSend(ControllerMode? saved, ControllerMode? hold) => hold ?? saved;

    /// <summary>Whether a name is one of the back paddle actions in <see cref="AutoTdpProtocol.PaddleActions"/>.</summary>
    public static bool IsPaddleAction(string? action) =>
        action is not null && AutoTdpProtocol.PaddleActions.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical paddle action name, or <see cref="AutoTdpProtocol.PaddleNone"/> when unknown or null.</summary>
    public static string ParsePaddleAction(string? action) =>
        AutoTdpProtocol.PaddleActions.FirstOrDefault(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase))
        ?? AutoTdpProtocol.PaddleNone;

    /// <summary>Whether a name is one of the thumbstick lighting modes in <see cref="AutoTdpProtocol.LightingModes"/>.</summary>
    public static bool IsLightingMode(string? lighting) =>
        lighting is not null && AutoTdpProtocol.LightingModes.Any(m => string.Equals(m, lighting, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical lighting mode name, or <see cref="AutoTdpProtocol.LightingMedium"/> when unknown or null.</summary>
    public static string ParseLighting(string? lighting) =>
        AutoTdpProtocol.LightingModes.FirstOrDefault(m => string.Equals(m, lighting, StringComparison.OrdinalIgnoreCase))
        ?? AutoTdpProtocol.LightingMedium;

    /// <summary>The hardware brightness step (0 to 3) for a lighting mode.</summary>
    public static byte BrightnessLevel(string lighting) => ParseLighting(lighting) switch
    {
        AutoTdpProtocol.LightingOff => 0,
        AutoTdpProtocol.LightingLow => 1,
        AutoTdpProtocol.LightingHigh => 3,
        _ => 2
    };

    /// <summary>Xbox green, the colour the lights start in.</summary>
    public static (byte R, byte G, byte B) DefaultColour => (0x10, 0x7C, 0x10);

    /// <summary>Whether a name is one of <see cref="AutoTdpProtocol.LightingColours"/>.</summary>
    public static bool IsLightingColour(string? colour) =>
        colour is not null && AutoTdpProtocol.LightingColours.Any(c => string.Equals(c, colour, StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour's own name, or Xbox green when it is not one.</summary>
    public static string ParseColour(string? colour) =>
        AutoTdpProtocol.LightingColours.FirstOrDefault(c => string.Equals(c, colour, StringComparison.OrdinalIgnoreCase))
        ?? AutoTdpProtocol.ColourGreen;

    /// <summary>
    /// What a colour sends. Full strength on one or two channels, as the rings' LEDs mix them: a little of
    /// another channel shows as a tint rather than as a softer shade.
    /// </summary>
    public static (byte R, byte G, byte B) Rgb(string? colour) => ParseColour(colour) switch
    {
        AutoTdpProtocol.ColourWhite => (0xFF, 0xFF, 0xFF),
        AutoTdpProtocol.ColourRed => (0xFF, 0x00, 0x00),
        AutoTdpProtocol.ColourOrange => (0xFF, 0x50, 0x00),
        AutoTdpProtocol.ColourYellow => (0xFF, 0xC8, 0x00),
        AutoTdpProtocol.ColourBlue => (0x00, 0x30, 0xFF),
        AutoTdpProtocol.ColourCyan => (0x00, 0xFF, 0xFF),
        AutoTdpProtocol.ColourPurple => (0x80, 0x00, 0xFF),
        AutoTdpProtocol.ColourPink => (0xFF, 0x00, 0x80),
        _ => DefaultColour
    };

    /// <summary>The stick ring colour for a battery percentage: red below 20%, amber below 50%, green from 50%.</summary>
    public static (byte R, byte G, byte B) BatteryColour(int? chargePercent) => chargePercent switch
    {
        < 20 => (0xE8, 0x11, 0x23),
        < 50 => (0xFF, 0xAA, 0x00),
        _ => DefaultColour
    };

    /// <summary>The feature report that sets the thumbstick light ring brightness (0 off, 1 low, 2 medium, 3 high).</summary>
    public static byte[] LightingBrightness(byte level)
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = 0xBA;
        report[2] = 0xC5;
        report[3] = 0xC4;
        report[4] = Math.Min(level, (byte)3);
        return report;
    }

    /// <summary>The feature report that sets the thumbstick light rings to a static RGB colour.</summary>
    public static byte[] LightingColour(byte r, byte g, byte b)
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = 0xB3;
        report[4] = r;
        report[5] = g;
        report[6] = b;
        return report;
    }

    /// <summary>The feature report that commits a lighting colour change on the Ally's controller.</summary>
    public static byte[] LightingSet()
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = 0xB5;
        return report;
    }

    /// <summary>The feature report that applies the committed lighting colour on the Ally's controller.</summary>
    public static byte[] LightingApply()
    {
        var report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = 0xB4;
        return report;
    }

    /// <summary>The sequence of feature reports to put the thumbstick light rings in the chosen mode and colour.</summary>
    /// <param name="colour">One of <see cref="AutoTdpProtocol.LightingColours"/>. Battery mode uses the charge's colour instead.</param>
    public static byte[][] LightingReports(string lighting, int? batteryPercent = null, string? colour = null)
    {
        var mode = ParseLighting(lighting);
        if (mode == AutoTdpProtocol.LightingOff)
        {
            return [LightingBrightness(0)];
        }

        var (r, g, b) = mode == AutoTdpProtocol.LightingBattery ? BatteryColour(batteryPercent) : Rgb(colour);
        return
        [
            LightingBrightness(BrightnessLevel(mode)),
            LightingColour(r, g, b),
            LightingSet(),
            LightingApply()
        ];
    }

    /// <summary>How long to wait before looking for the controller again: 2 seconds, doubling, up to 30.</summary>
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(attempt >= 4 ? 30 : 2 << Math.Max(attempt, 0));
}
