namespace HandheldOptimiser.PowerService;

/// <summary>What the player asked Auto TDP for.</summary>
/// <param name="TargetFps">The frame rate to hold.</param>
/// <param name="MinWatts">The lowest power limit it may go to.</param>
/// <param name="MaxWatts">The highest power limit it may go to.</param>
/// <param name="TemperatureLimit">The processor temperature, in °C, it lowers the power limit at, whatever the frame rate.</param>
internal sealed record AutoTdpSettings(int TargetFps, int MinWatts, int MaxWatts, int TemperatureLimit);

/// <summary>
/// Finds the lowest power limit that holds a target frame rate. It is fed one reading a second and
/// answers with a new power limit when one is due. No hardware here, so it can be tested on its own.
///
/// Controllers like this tend to go wrong in a few known ways, and each has its own guard:
/// - Chasing noise. Decisions use three seconds of readings, a change waits for its effect before the
///   next one, and there is a band just under the target where nothing changes.
/// - Sawing at an edge. A game capped at the target sits there at any power limit above the edge, so
///   stepping down eventually costs frames, and a noisy frame rate near the target does the same. Each
///   raise becomes a floor that is not stepped below for 30 seconds, doubling up to 4 minutes when the
///   raise came soon after a step down.
/// - Burning power for nothing. A game held back by its processor or its own cap gains nothing from
///   more power. Two raises in a row that gain under 3% are taken back and the limit held for a minute.
/// - Reacting to loading screens and pauses. Under 5 frames a second nothing changes.
/// - Heat. At the temperature limit the power limit comes down whatever the frame rate, and climbs back
///   only a watt every 20 seconds once it has cooled a few degrees.
/// </summary>
internal sealed class AutoTdpController
{
    private const int Window = 3;
    private const double MinFrames = 5;

    // Below this share of the target the power goes up; at or above Comfortable it may come down.
    private const double Below = 0.97;
    private const double Comfortable = 0.99;
    private const double SteadyLow = 0.97;

    // A raise has to gain this much to count.
    private const double Gain = 1.03;
    private const int WastedRaises = 2;

    private const int CoolBy = 3;
    private const int HeatStep = 2;

    private static readonly TimeSpan SettleAfterRaise = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettleAfterLower = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SettleAfterHeat = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstFloorHold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestFloorHold = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RecentlyLowered = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoGainHold = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CoolRaiseEvery = TimeSpan.FromSeconds(20);

    private readonly AutoTdpSettings _settings;
    private readonly Queue<double> _readings = new();

    private DateTime _nextChange = DateTime.MinValue;

    // The last change and the frame rate before it, judged once it has had time to work.
    private (bool Raise, int From, double FpsBefore)? _judging;

    // The game needed this much, so the limit is not stepped below it until the time given.
    private int _floor;
    private DateTime _lastLowered = DateTime.MinValue;
    private DateTime _floorUntil = DateTime.MinValue;
    private TimeSpan _floorHold = FirstFloorHold;

    // Raises that gained nothing, and the limit they started from.
    private int _wasted;
    private int _wastedFrom;
    private int _noGainCap;
    private DateTime _noGainUntil = DateTime.MinValue;

    // Set by heat: the limit stays at or under this until it has cooled, then climbs back slowly.
    private bool _hot;
    private int? _heatCeiling;
    private DateTime _nextCoolRaise = DateTime.MinValue;

    public AutoTdpController(AutoTdpSettings settings, int startWatts)
    {
        _settings = settings;
        Watts = Math.Clamp(startWatts, settings.MinWatts, settings.MaxWatts);
        _floor = settings.MinWatts;
    }

    /// <summary>The power limit in watts it wants right now.</summary>
    public int Watts { get; private set; }

    public AutoTdpState State { get; private set; } = AutoTdpState.Settling;

    /// <summary>One second's reading.</summary>
    /// <param name="fps">The game's frames in the last second.</param>
    /// <param name="temperature">The processor's temperature in °C, or null when it cannot be read.</param>
    /// <returns>The new power limit when it should change, otherwise null.</returns>
    public int? Tick(DateTime now, double fps, double? temperature)
    {
        _readings.Enqueue(fps);
        while (_readings.Count > Window)
        {
            _readings.Dequeue();
        }

        if (CoolDown(now, temperature) is { } cooler)
        {
            return cooler;
        }

        if (_hot)
        {
            return null;
        }

        if (fps < MinFrames)
        {
            // Whatever was being judged is lost in the pause.
            _readings.Clear();
            _judging = null;
            State = AutoTdpState.Waiting;
            return null;
        }

        if (_readings.Count < Window || now < _nextChange)
        {
            State = AutoTdpState.Settling;
            return null;
        }

        var average = _readings.Average();
        var low = _readings.Min();
        var target = (double)_settings.TargetFps;

        if (Judge(now, average) is { } takenBack)
        {
            return takenBack;
        }

        if (average < target * Below)
        {
            var ceiling = Ceiling(now);
            if (Watts >= ceiling)
            {
                State = now < _noGainUntil ? AutoTdpState.NoGain : AutoTdpState.AtMaximum;
                return null;
            }

            var shortBy = (target - average) / target;
            var step = shortBy > 0.25 ? 3 : shortBy > 0.12 ? 2 : 1;
            var raised = Math.Min(Watts + step, ceiling);

            // What the game needs for now, so not stepped below until the hold is over. Short of frames
            // soon after a step down means that step went too far, so the hold doubles each time.
            _floorHold = now - _lastLowered < RecentlyLowered
                ? (_floorHold * 2 > LongestFloorHold ? LongestFloorHold : _floorHold * 2)
                : FirstFloorHold;
            _floor = raised;
            _floorUntil = now + _floorHold;

            State = AutoTdpState.Raising;
            _judging = (true, Watts, average);
            return Change(now, raised, SettleAfterRaise);
        }

        if (average >= target * Comfortable && low >= target * SteadyLow)
        {
            var floor = now < _floorUntil ? Math.Max(_floor, _settings.MinWatts) : _settings.MinWatts;
            if (Watts <= floor)
            {
                State = AutoTdpState.Holding;
                return null;
            }

            var spare = (average - target) / target;
            var step = spare > 0.25 ? 2 : 1;
            State = AutoTdpState.Lowering;
            _judging = (false, Watts, average);
            _lastLowered = now;
            return Change(now, Math.Max(Watts - step, floor), SettleAfterLower);
        }

        State = AutoTdpState.Holding;
        return null;
    }

    /// <summary>Lowers the limit at the temperature limit, and lets it back up slowly once cooler.</summary>
    private int? CoolDown(DateTime now, double? temperature)
    {
        if (temperature is not { } degrees)
        {
            return null;
        }

        if (degrees >= _settings.TemperatureLimit)
        {
            _hot = true;
        }
        else if (degrees <= _settings.TemperatureLimit - CoolBy)
        {
            _hot = false;
        }

        if (_hot)
        {
            State = AutoTdpState.TooHot;
            _judging = null;
            if (now >= _nextChange && Watts > _settings.MinWatts)
            {
                var cooler = Math.Max(Watts - HeatStep, _settings.MinWatts);
                _heatCeiling = cooler;
                _nextCoolRaise = now + CoolRaiseEvery;
                _readings.Clear();
                return Change(now, cooler, SettleAfterHeat);
            }

            return null;
        }

        // Cooled: the ceiling rises a watt at a time, so it does not run straight back into the limit.
        if (_heatCeiling is { } ceiling && now >= _nextCoolRaise)
        {
            _heatCeiling = ceiling + 1 >= _settings.MaxWatts ? null : ceiling + 1;
            _nextCoolRaise = now + CoolRaiseEvery;
        }

        return null;
    }

    /// <summary>Weighs the last change now it has had time to work. Returns a limit when a change is taken back.</summary>
    private int? Judge(DateTime now, double average)
    {
        if (_judging is not { } last)
        {
            return null;
        }

        _judging = null;
        if (last.Raise)
        {
            if (average >= last.FpsBefore * Gain)
            {
                _wasted = 0;
                return null;
            }

            if (_wasted++ == 0)
            {
                _wastedFrom = last.From;
            }

            if (_wasted < WastedRaises)
            {
                return null;
            }

            // More power did nothing: back to where the raises started, and stay there a while.
            _wasted = 0;
            _noGainCap = _wastedFrom;
            _noGainUntil = now + NoGainHold;
            State = AutoTdpState.NoGain;
            return _wastedFrom < Watts ? Change(now, _wastedFrom, SettleAfterLower) : null;
        }

        return null;
    }

    private int Ceiling(DateTime now)
    {
        var ceiling = _settings.MaxWatts;
        if (now < _noGainUntil)
        {
            ceiling = Math.Min(ceiling, _noGainCap);
        }

        if (_heatCeiling is { } heat)
        {
            ceiling = Math.Min(ceiling, heat);
        }

        return ceiling;
    }

    private int? Change(DateTime now, int watts, TimeSpan settle)
    {
        watts = Math.Clamp(watts, _settings.MinWatts, _settings.MaxWatts);
        _nextChange = now + settle;
        _readings.Clear();
        if (watts == Watts)
        {
            return null;
        }

        Watts = watts;
        return watts;
    }
}
