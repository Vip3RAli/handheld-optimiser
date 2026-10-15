namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Dynamic fan choice: which of the fixed curves to run, from the temperature and the power limit.
/// A curve already follows the temperature, so this adds what the firmware cannot see. It steps up as soon
/// as the power limit jumps, before the heat arrives, and it holds a higher curve until a lower one has
/// been right for <see cref="Hold"/>, so the fans do not keep surging up and down. Pure: the service
/// asks it once a tick and writes the curve when the answer changes.
/// </summary>
internal sealed class DynamicFans
{
    /// <summary>How long a lower curve has to be right before the fans step down to it.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);

    /// <summary>How quickly the power limit has to rise by <see cref="JumpWatts"/> to count as a jump.</summary>
    public static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(10);

    public const int JumpWatts = 5;

    // Below the temperature limit by these many degrees, the fans step up to Cool, then to Full.
    private const int CoolBelowLimit = 10;
    private const int FullBelowLimit = 3;

    // A power limit at this share of the highest allowed is high enough to want Cool at least.
    private const int HighPowerPercent = 70;

    private static readonly string[] Levels = [AutoTdpProtocol.QuietFans, AutoTdpProtocol.CoolFans, AutoTdpProtocol.FullFans];

    private readonly Queue<(DateTime At, int Watts)> _recentWatts = new();
    private int _level = -1;
    private DateTime? _lowerSince;

    /// <summary>The curve to run now.</summary>
    /// <param name="temperature">The processor's temperature, or null when it could not be read.</param>
    /// <param name="watts">The power limit TDP Engine set, or null when it only sets the fans.</param>
    /// <param name="maxWatts">The highest power limit allowed, or 0 when it only sets the fans.</param>
    /// <param name="temperatureLimit">TDP Engine's temperature limit, which the thresholds are measured from.</param>
    /// <returns>One of <see cref="AutoTdpProtocol.FanCurves"/>.</returns>
    public string Pick(DateTime now, int? temperature, int? watts, int maxWatts, int temperatureLimit)
    {
        // Without a temperature it keeps what it has, or starts in the middle.
        var wanted = temperature is { } degrees ? ForTemperature(degrees, temperatureLimit) : Math.Max(_level, 1);

        if (watts is { } power && maxWatts > 0)
        {
            if (power * 100 >= maxWatts * HighPowerPercent)
            {
                wanted = Math.Max(wanted, 1);
            }

            if (Jumped(now, power))
            {
                wanted = Math.Max(wanted, Math.Min(_level + 1, Levels.Length - 1));
            }
        }

        Step(now, wanted);
        return Levels[_level];
    }

    private static int ForTemperature(int degrees, int limit) =>
        degrees >= limit - FullBelowLimit ? 2
        : degrees >= limit - CoolBelowLimit ? 1
        : 0;

    /// <summary>Whether the power limit rose by <see cref="JumpWatts"/> within <see cref="JumpWindow"/>.</summary>
    private bool Jumped(DateTime now, int watts)
    {
        while (_recentWatts.Count > 0 && now - _recentWatts.Peek().At > JumpWindow)
        {
            _recentWatts.Dequeue();
        }

        var jumped = _recentWatts.Count > 0 && watts - _recentWatts.Min(reading => reading.Watts) >= JumpWatts;

        // A jump counts once: the readings before it go, so the next tick does not step up again for the same jump.
        if (jumped)
        {
            _recentWatts.Clear();
        }

        _recentWatts.Enqueue((now, watts));
        return jumped;
    }

    private void Step(DateTime now, int wanted)
    {
        if (_level < 0 || wanted > _level)
        {
            _level = wanted;
            _lowerSince = null;
        }
        else if (wanted == _level)
        {
            _lowerSince = null;
        }
        else if (_lowerSince is not { } since)
        {
            _lowerSince = now;
        }
        else if (now - since >= Hold)
        {
            _level = wanted;
            _lowerSince = null;
        }
    }
}
