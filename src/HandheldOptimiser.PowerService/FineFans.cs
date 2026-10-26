namespace HandheldOptimiser.PowerService;

/// <summary>
/// The fans in Auto: a step from 0 to 10, written as <see cref="FanCurves.Blend"/>'s curve. Finer than
/// Dynamic's three curves, so the fans can rise a little for boost without jumping to full. It follows the
/// temperature, steps up as soon as the power limit jumps or boost turns on, before the heat arrives, and
/// comes down one step at a time, so the fans ease off rather than drop. Pure: asked once a tick.
/// </summary>
internal sealed class FineFans
{
    public const int Steps = 10;

    /// <summary>How long a lower step has to be right before the fans come down one step.</summary>
    public static readonly TimeSpan StepDownAfter = TimeSpan.FromSeconds(15);

    // The temperature range the steps spread over, in degrees under TDP Engine's temperature limit: step 0 at
    // the first and 10 at the second, where Dynamic goes to Full.
    private const int QuietUnderLimit = 20;
    private const int FullUnderLimit = 3;

    // At this share of the top power limit, the fans run at Cool's step at least, as Dynamic does.
    private const int HighPowerPercent = 70;
    private const int HighPowerStep = 5;

    // A rise of the power limit this big this fast, or boost turning on, lifts the fans this many steps.
    private const int JumpWatts = 5;
    private static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(10);
    private const int EarlySteps = 2;

    // While boost is on, the fans never run below this step.
    private const int BoostFloor = 4;

    private readonly Queue<(DateTime At, int Watts)> _recentWatts = new();
    private int _step = -1;
    private bool _boost;
    private DateTime? _lowerSince;

    /// <summary>The step to run now, 0 to <see cref="Steps"/>.</summary>
    /// <param name="temperature">The processor's temperature, or null when it could not be read.</param>
    /// <param name="watts">The power limit set now, or null when none is.</param>
    /// <param name="topWatts">The top of the power range now.</param>
    /// <param name="temperatureLimit">TDP Engine's temperature limit, which the steps are measured from.</param>
    /// <param name="boost">Whether the processor's boost is on.</param>
    public int Pick(DateTime now, int? temperature, int? watts, int topWatts, int temperatureLimit, bool boost)
    {
        // Without a temperature it keeps what it has, or starts in the middle.
        var wanted = temperature is { } degrees ? ForTemperature(degrees, temperatureLimit) : Math.Max(_step, HighPowerStep);

        if (watts is { } power && topWatts > 0)
        {
            if (power * 100 >= topWatts * HighPowerPercent)
            {
                wanted = Math.Max(wanted, HighPowerStep);
            }

            if (Jumped(now, power))
            {
                wanted = Math.Max(wanted, Math.Max(_step, 0) + EarlySteps);
            }
        }

        if (boost && !_boost)
        {
            wanted = Math.Max(wanted, Math.Max(_step, 0) + EarlySteps);
        }

        _boost = boost;
        if (boost)
        {
            wanted = Math.Max(wanted, BoostFloor);
        }

        Step(now, Math.Clamp(wanted, 0, Steps));
        return _step;
    }

    private static int ForTemperature(int degrees, int limit)
    {
        var quiet = limit - QuietUnderLimit;
        var full = limit - FullUnderLimit;
        return degrees <= quiet ? 0
            : degrees >= full ? Steps
            : (int)Math.Round((double)(degrees - quiet) * Steps / (full - quiet), MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether the power limit rose by <see cref="JumpWatts"/> within <see cref="JumpWindow"/>. A jump counts once.</summary>
    private bool Jumped(DateTime now, int watts)
    {
        while (_recentWatts.Count > 0 && now - _recentWatts.Peek().At > JumpWindow)
        {
            _recentWatts.Dequeue();
        }

        var jumped = _recentWatts.Count > 0 && watts - _recentWatts.Min(reading => reading.Watts) >= JumpWatts;
        if (jumped)
        {
            _recentWatts.Clear();
        }

        _recentWatts.Enqueue((now, watts));
        return jumped;
    }

    private void Step(DateTime now, int wanted)
    {
        if (_step < 0 || wanted > _step)
        {
            _step = wanted;
            _lowerSince = null;
        }
        else if (wanted == _step)
        {
            _lowerSince = null;
        }
        else if (_lowerSince is not { } since)
        {
            _lowerSince = now;
        }
        else if (now - since >= StepDownAfter)
        {
            // One step, and the next waits its own 15 seconds.
            _step--;
            _lowerSince = _step > wanted ? now : null;
        }
    }
}
