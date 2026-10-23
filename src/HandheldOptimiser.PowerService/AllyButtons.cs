namespace HandheldOptimiser.PowerService;

/// <summary>A button on the Ally's front panel, as its input report names it.</summary>
internal enum AllyButton
{
    /// <summary>Not a report the service acts on.</summary>
    None,
    ArmouryCrate,
    CommandCenter,
    /// <summary>A back paddle.</summary>
    Paddle,
    /// <summary>Every button is up again.</summary>
    Release,
}

/// <summary>Reads the Ally's input reports. A report is the report ID 0x5A, then a code for the button.</summary>
internal static class AllyButtons
{
    private const byte ReportId = 0x5A;

    /// <summary>What a report says, or None when it is too short, has another ID or carries an unknown code.</summary>
    public static AllyButton Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 2 || report[0] != ReportId)
        {
            return AllyButton.None;
        }

        return report[1] switch
        {
            0x38 => AllyButton.ArmouryCrate,
            0xA6 => AllyButton.CommandCenter,
            0xA5 => AllyButton.Paddle,
            0x00 => AllyButton.Release,
            _ => AllyButton.None,
        };
    }
}

/// <summary>
/// Turns reports into presses. The Ally can send one press twice in quick succession, so the same button
/// counts again only 300 ms after its last counted press.
/// </summary>
internal sealed class AllyButtonFilter
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<AllyButton, DateTime> _lastCounted = [];

    /// <summary>The button when this report is a new press, otherwise null (a release, an unknown code or a repeat).</summary>
    public AllyButton? Press(ReadOnlySpan<byte> report, DateTime now) => Press(AllyButtons.Parse(report), now);

    /// <summary>The button when it is a new press, otherwise null (a release, no button or a repeat).</summary>
    public AllyButton? Press(AllyButton button, DateTime now)
    {
        if (button is not (AllyButton.ArmouryCrate or AllyButton.CommandCenter or AllyButton.Paddle))
        {
            return null;
        }

        if (_lastCounted.TryGetValue(button, out var last) && now - last < Window)
        {
            return null;
        }

        _lastCounted[button] = now;
        return button;
    }
}
