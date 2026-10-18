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

    /// <summary>How long to wait before looking for the controller again: 2 seconds, doubling, up to 30.</summary>
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(attempt >= 4 ? 30 : 2 << Math.Max(attempt, 0));
}
