namespace HandheldOptimiser.PowerService;

/// <summary>What the player asked TDP Engine for.</summary>
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
/// - Chasing noise. Decisions use the middle of three seconds of readings, a change waits for its effect
///   before the next one, and there is a band just under the target where nothing changes.
/// - Chasing hitches and spikes. Some games jump between a few frames and hundreds from one second to
///   the next (level changes, uncapped menus, stutters). A second that far from the others is not
///   something power fixes, so nothing changes until the readings agree again.
/// - Sawing at an edge. A game capped at the target sits there at any power limit above the edge, so
///   stepping down eventually costs frames, and a noisy frame rate near the target does the same. Each
///   raise becomes a floor that is not stepped below for 30 seconds, doubling up to 4 minutes when the
///   raise came soon after a step down.
/// - Burning power for nothing. A game held back by its processor or its own cap gains nothing from
///   more power. Two raises in a row that gain under 3% are taken back and the limit held for a minute.
///   A game that is already at the top limit has no raises to judge, as when a game idles at a menu
///   that runs at 30 fps. After a minute there, short of the target, it tries a quarter less. If that
///   costs under 10% of the frame rate the lower limit is held as above, and it keeps stepping down
///   while nothing is lost. A step that costs frames is taken back, and the next try waits twice as long.
///   An idle menu's frame rate wanders far from one second to the next, so these steps are judged on
///   10 seconds, the two highest and two lowest left out, rather than the middle of 3.
/// - Holding a cap the game has outgrown. When a hold runs out, one step up of a quarter decides: a gain
///   of 15% hands the game back to the usual raises, and anything less holds the limit again. A hold is
///   also let go when the frame rate has moved 20% from where it was held for 5 seconds, as when the
///   player goes from a menu back into a match.
/// - Reacting to loading screens and pauses. Under 5 frames a second nothing changes.
/// - Heat. At the temperature limit the power limit comes down whatever the frame rate, and climbs back
///   only a watt every 20 seconds once it has cooled a few degrees.
/// </summary>
internal sealed class AutoTdpController
{
    private const int Window = 3;
    private const double MinFrames = 5;

    // Readings this far from the middle one, below or above, make the window too unsteady to act on.
    private const double Unsteady = 2;

    // Below this share of the target the power goes up; at or above Comfortable it may come down.
    private const double Below = 0.97;
    private const double Comfortable = 0.99;
    private const double SteadyLow = 0.97;

    // A raise has to gain this much to count.
    private const double Gain = 1.03;
    private const int WastedRaises = 2;

    // A step down from the top is judged on the middle of 10 seconds rather than 3, and may cost up to 10%:
    // an idle menu's frame rate swings more than that from one second to the next. A held limit is let go
    // when the frame rate has moved 20% from where it was held for 5 seconds running.
    private const int StepDownWindow = 10;
    private const double StepDownLoss = 0.90;
    private const double HeldMoved = 0.20;

    // A step down takes a quarter of the limit, at least 2 W: big enough that a game the power is helping
    // loses well over the 10% allowed, where a game that gains nothing from it loses nothing.
    private const int StepDownShare = 4;
    private const int SmallestStepDown = 2;

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
    private static readonly TimeSpan HeldMovedFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstStepDownWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestStepDownWait = TimeSpan.FromMinutes(4);

    private readonly AutoTdpSettings _settings;
    private readonly Queue<double> _readings = new();

    // The same readings over a longer window, for judging a step down from the top.
    private readonly Queue<double> _longReadings = new();

    private DateTime _nextChange = DateTime.MinValue;

    // The last change and the frame rate before it, judged once it has had time to work.
    private (Step Kind, int From, double FpsBefore)? _judging;

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

    // The frame rate when the limit was held, to let it go when the game changes, and since when it has
    // been far from it.
    private double _noGainFps;
    private DateTime? _movedSince;

    // Short of the target at the top limit: since when, how long before a step down is tried, and
    // whether a step down just cost nothing, so the next one comes without waiting.
    private DateTime? _atTopSince;
    private TimeSpan _stepDownWait = FirstStepDownWait;
    private bool _steppingDown;

    // A limit is held because power bought nothing, so it is checked with a step up when the hold runs out.
    private bool _heldNoGain;

    // A step up has to gain this much to show the power helps again: a game that needs the power gains
    // about 20% from a quarter more watts, where an idle menu's middle reading wanders by 10%.
    private const double StepUpGain = 1.15;

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

    /// <summary>The frame rate its last decision went on: the middle of the last few seconds, not one second's count.</summary>
    public double? DecidedFps { get; private set; }

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

        _longReadings.Enqueue(fps);
        while (_longReadings.Count > StepDownWindow)
        {
            _longReadings.Dequeue();
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
            _longReadings.Clear();
            _judging = null;
            State = AutoTdpState.Waiting;
            return null;
        }

        if (_readings.Count < Window || now < _nextChange)
        {
            State = AutoTdpState.Settling;
            return null;
        }

        var sorted = _readings.Order().ToArray();
        var middle = sorted[sorted.Length / 2];
        var low = sorted[0];
        var target = (double)_settings.TargetFps;

        if (low * Unsteady < middle || sorted[^1] > middle * Unsteady)
        {
            // A hitch or a spike: whatever was being judged is lost in it too.
            _judging = null;
            State = AutoTdpState.Settling;
            return null;
        }

        DecidedFps = middle;

        // Steps down and back up are judged on the longer window, so they wait until it has filled.
        var longJudge = _judging is { Kind: Step.Down or Step.Up };
        if (longJudge && _longReadings.Count < StepDownWindow)
        {
            State = AutoTdpState.Settling;
            return null;
        }

        if (Judge(now, longJudge ? LongMiddle() : middle) is { } takenBack)
        {
            return takenBack;
        }

        // Measured on the longer window too: an idle menu's single seconds stray 20% all the time.
        if (now < _noGainUntil && _longReadings.Count >= StepDownWindow
            && Math.Abs(LongMiddle() - _noGainFps) > _noGainFps * HeldMoved)
        {
            // The game changed, so what power could not do before it may do now.
            _movedSince ??= now;
            if (now - _movedSince >= HeldMovedFor)
            {
                _noGainUntil = DateTime.MinValue;
                _steppingDown = false;
                _heldNoGain = false;
                _movedSince = null;
            }
        }
        else
        {
            _movedSince = null;
        }

        if (middle < target * Below)
        {
            // A held limit is not let go on a timer, which would hand it back to raises judged on three
            // noisy seconds. One step up, judged like the steps down, decides.
            if (_heldNoGain && now >= _noGainUntil)
            {
                return StepUp(now);
            }

            var ceiling = Ceiling(now);
            if (Watts >= ceiling)
            {
                State = now < _noGainUntil ? AutoTdpState.NoGain : AutoTdpState.AtMaximum;
                return StepDown(now, middle);
            }

            _atTopSince = null;

            var shortBy = (target - middle) / target;
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
            _judging = (Step.Raise, Watts, middle);
            return Change(now, raised, SettleAfterRaise);
        }

        // At the target, so whatever was held from the top no longer matters.
        _atTopSince = null;
        _heldNoGain = false;
        if (middle >= target * Comfortable && low >= target * SteadyLow)
        {
            var floor = now < _floorUntil ? Math.Max(_floor, _settings.MinWatts) : _settings.MinWatts;
            if (Watts <= floor)
            {
                State = AutoTdpState.Holding;
                return null;
            }

            var spare = (middle - target) / target;
            var step = spare > 0.25 ? 2 : 1;
            State = AutoTdpState.Lowering;
            _judging = (Step.Lower, Watts, middle);
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

            // Only at or over the limit does it cut. Between the limit and a few degrees under it, the cut
            // so far is holding the temperature, so it holds too rather than sliding on down to the minimum.
            if (degrees >= _settings.TemperatureLimit && now >= _nextChange && Watts > _settings.MinWatts)
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
    private int? Judge(DateTime now, double fps)
    {
        if (_judging is not { } last)
        {
            return null;
        }

        _judging = null;
        if (last.Kind == Step.Down)
        {
            if (fps >= last.FpsBefore * StepDownLoss)
            {
                // Nothing lost: held here, and the next step down comes as soon as this one has settled.
                Hold(now, Watts, fps);
                _steppingDown = true;
                _stepDownWait = FirstStepDownWait;
                State = AutoTdpState.NoGain;
                return null;
            }

            // It cost frames: back up, and wait longer before trying again.
            _steppingDown = false;
            _atTopSince = now;
            _stepDownWait = _stepDownWait * 2 > LongestStepDownWait ? LongestStepDownWait : _stepDownWait * 2;
            if (now < _noGainUntil)
            {
                _noGainCap = last.From;
            }

            return Change(now, last.From, SettleAfterRaise);
        }

        if (last.Kind == Step.Up)
        {
            if (fps >= last.FpsBefore * StepUpGain)
            {
                // The power helps again: back to the usual raises and steps down.
                _heldNoGain = false;
                _steppingDown = false;
                return null;
            }

            // Still nothing for it: back down, and held there for another while.
            Hold(now, last.From, last.FpsBefore);
            State = AutoTdpState.NoGain;
            return Change(now, last.From, SettleAfterLower);
        }

        if (last.Kind == Step.Raise)
        {
            if (fps >= last.FpsBefore * Gain)
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
            Hold(now, _wastedFrom, fps);
            State = AutoTdpState.NoGain;
            return _wastedFrom < Watts ? Change(now, _wastedFrom, SettleAfterLower) : null;
        }

        return null;
    }

    /// <summary>
    /// Keeps the limit at or under this for <see cref="NoGainHold"/>, or until the frame rate moves. When
    /// the hold runs out, a step up decides whether the power helps again.
    /// </summary>
    private void Hold(DateTime now, int watts, double fps)
    {
        _noGainCap = watts;
        _noGainUntil = now + NoGainHold;
        _noGainFps = fps;
        _heldNoGain = true;
    }

    /// <summary>
    /// Short of the target at the highest limit it may use: after a while there, tries a step down to see
    /// whether the power is buying anything. Returns the lower limit when it tries one.
    /// </summary>
    private int? StepDown(DateTime now, double fps)
    {
        if (Watts <= _settings.MinWatts)
        {
            return null;
        }

        _atTopSince ??= now;
        if (!_steppingDown && now - _atTopSince < _stepDownWait)
        {
            return null;
        }

        // Judged against the same longer window, taken before the step.
        _judging = (Step.Down, Watts, _longReadings.Count >= StepDownWindow ? LongMiddle() : fps);
        return Change(now, Watts - Math.Max(SmallestStepDown, Watts / StepDownShare), SettleAfterLower);
    }

    /// <summary>When a limit stepped down to from the top runs out: one step up, to see whether the power helps again.</summary>
    private int? StepUp(DateTime now)
    {
        if (Watts >= _settings.MaxWatts)
        {
            _heldNoGain = false;
            return null;
        }

        State = AutoTdpState.NoGain;
        _judging = (Step.Up, Watts, _longReadings.Count >= StepDownWindow ? LongMiddle() : DecidedFps ?? 0);
        return Change(now, Watts + Math.Max(SmallestStepDown, Watts / StepDownShare), SettleAfterLower);
    }

    /// <summary>
    /// The longer window's frame rate: the average once its two highest and two lowest seconds are dropped.
    /// Steadier than the middle reading when every second wanders, as at an idle menu, and a hitch or a
    /// spike still cannot pull it.
    /// </summary>
    private double LongMiddle()
    {
        var sorted = _longReadings.Order().ToArray();
        var trim = sorted.Length >= StepDownWindow ? 2 : 0;
        return sorted[trim..^trim].Average();
    }

    private enum Step
    {
        Raise,
        Lower,

        /// <summary>A step down from the top, to see whether the power was buying anything.</summary>
        Down,

        /// <summary>A step back up once a limit stepped down to has been held, to see whether the power helps again.</summary>
        Up
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
        _longReadings.Clear();
        if (watts == Watts)
        {
            return null;
        }

        Watts = watts;
        return watts;
    }
}
