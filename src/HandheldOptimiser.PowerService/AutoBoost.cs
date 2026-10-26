namespace HandheldOptimiser.PowerService;

/// <summary>
/// The processor's boost in Auto. Boost buys short bursts of speed for heat and battery, so it is off unless
/// the game needs it: always off on battery, and plugged in, on only once the game falls short of its target
/// (or, with no target, sits at the top power limit), and off again once there is plenty of headroom. A change
/// waits 20 seconds after the last, so it does not flick on and off, except turning off when unplugged. Pure:
/// asked once a tick; it answers with the new state, or null to leave it.
/// </summary>
/// <param name="startsOn">Whether boost was on as the session started.</param>
internal sealed class AutoBoost(bool startsOn)
{
    /// <summary>How long between changes, except turning off when unplugged.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(20);

    // Short of the target by this many frames, or within this many watts of the top with no target, for this
    // long: on.
    private const double ShortFps = 3;
    private const int AtTopWatts = 1;
    private static readonly TimeSpan NeedFor = TimeSpan.FromSeconds(5);

    // Below this many frames a second nothing is judged, as in the power controller.
    private const double MinFrames = 5;

    // This many watts under the top, with the target held within this many frames, for this long: off.
    private const int HeadroomWatts = 3;
    private const double HeldFps = 1;
    private static readonly TimeSpan HeadroomFor = TimeSpan.FromSeconds(30);

    private bool _on = startsOn;
    private bool _started;
    private DateTime? _lastChange;
    private DateTime? _needSince;
    private DateTime? _headroomSince;

    /// <param name="fps">The game's frame rate now.</param>
    /// <param name="targetFps">The frame rate to hold, or 0 for none.</param>
    /// <param name="watts">The power limit set now.</param>
    /// <param name="topWatts">The top of the power range now.</param>
    /// <returns>True to turn it on, false to turn it off, or null to leave it.</returns>
    public bool? Decide(DateTime now, bool onBattery, double fps, int targetFps, int watts, int topWatts)
    {
        var first = !_started;
        _started = true;

        // On battery it is never wanted.
        if (onBattery)
        {
            _needSince = null;
            _headroomSince = null;
            return _on ? Change(now, false) : null;
        }

        // Plugged in, a session starts with it off until the game needs it.
        if (first && _on)
        {
            return Change(now, false);
        }

        // Under a few frames is a loading screen, a pause or no game yet: it says nothing either way.
        var playing = fps >= MinFrames;
        var needed = playing && (targetFps > 0 ? fps <= targetFps - ShortFps : watts >= topWatts - AtTopWatts);
        var headroom = playing && watts <= topWatts - HeadroomWatts && (targetFps <= 0 || fps >= targetFps - HeldFps);
        _needSince = needed ? _needSince ?? now : null;
        _headroomSince = headroom ? _headroomSince ?? now : null;

        if (_lastChange is { } last && now - last < Gap)
        {
            return null;
        }

        if (!_on && _needSince is { } since && now - since >= NeedFor)
        {
            return Change(now, true);
        }

        if (_on && _headroomSince is { } spare && now - spare >= HeadroomFor)
        {
            return Change(now, false);
        }

        return null;
    }

    private bool Change(DateTime now, bool on)
    {
        _on = on;
        _lastChange = now;
        _needSince = null;
        _headroomSince = null;
        return on;
    }
}
