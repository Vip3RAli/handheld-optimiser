using System.Windows.Input;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

internal enum GamepadAction
{
    Accept,
    Back,
    Options,
    QuickSettings,
    PreviousFilter,
    NextFilter,
    PageUp,
    PageDown,
    Menu,
    View
}

/// <summary>
/// Polls XInput controllers while the library is in front, so the d-pad or left stick moves between
/// tiles, A launches, X opens a game's options, Y opens search, LT/RT page through lists, and the bumpers
/// change the store filter. Polling stops whenever the library loses focus: the game owns the controller
/// then, and the library must not react to in-game presses.
/// </summary>
internal sealed class GamepadInput
{
    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;
    private const ushort AllDpad = DpadUp | DpadDown | DpadLeft | DpadRight;

    private static readonly (ushort Bit, GamepadAction Action)[] Buttons =
    [
        (0x1000, GamepadAction.Accept),
        (0x2000, GamepadAction.Back),
        (0x4000, GamepadAction.Options),
        (0x8000, GamepadAction.QuickSettings),
        (0x0100, GamepadAction.PreviousFilter),
        (0x0200, GamepadAction.NextFilter),
        (0x0010, GamepadAction.Menu),
        (0x0020, GamepadAction.View)
    ];

    private const short StickEngage = 10500;
    private const short StickRelease = 7800;
    private const byte TriggerEngage = 120;
    private const byte TriggerRelease = 80;
    private const uint MaxControllers = 4;

    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan RepeatNormal = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan RepeatMedium = TimeSpan.FromMilliseconds(65);
    private static readonly TimeSpan RepeatFast = TimeSpan.FromMilliseconds(40);

    // XInputGetState is slow for empty slots, so those are only retried now and then.
    private static readonly TimeSpan DisconnectedRetry = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private readonly DateTime[] _nextProbe = new DateTime[MaxControllers];
    private readonly bool[] _connected = new bool[MaxControllers];

    private ushort _previous;
    private FocusNavigationDirection? _heldDirection;
    private ushort _heldStickBit;
    private DateTime _heldStartedAt;
    private DateTime _nextRepeat;
    private bool _leftTriggerDown;
    private bool _rightTriggerDown;

    public event Action<FocusNavigationDirection>? Navigate;
    public event Action<GamepadAction>? Pressed;

    public GamepadInput(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Poll(), dispatcher);
        _timer.Stop();
    }

    public void Start()
    {
        // Buttons already held when focus returns (such as the A that closed a game menu) are not presses.
        _previous = ReadButtons(out _, out _);
        _heldDirection = null;
        _heldStickBit = 0;
        _leftTriggerDown = false;
        _rightTriggerDown = false;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var buttons = ReadButtons(out var leftTrigger, out var rightTrigger);
        var pressed = (ushort)(buttons & ~_previous);
        _previous = buttons;

        foreach (var (bit, action) in Buttons)
        {
            if ((pressed & bit) != 0)
            {
                Pressed?.Invoke(action);
            }
        }

        if (!_leftTriggerDown && leftTrigger >= TriggerEngage)
        {
            _leftTriggerDown = true;
            Pressed?.Invoke(GamepadAction.PageUp);
        }
        else if (_leftTriggerDown && leftTrigger < TriggerRelease)
        {
            _leftTriggerDown = false;
        }

        if (!_rightTriggerDown && rightTrigger >= TriggerEngage)
        {
            _rightTriggerDown = true;
            Pressed?.Invoke(GamepadAction.PageDown);
        }
        else if (_rightTriggerDown && rightTrigger < TriggerRelease)
        {
            _rightTriggerDown = false;
        }

        var direction = DirectionOf(buttons);
        var now = DateTime.UtcNow;

        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            if (direction is { } first)
            {
                _heldStartedAt = now;
                Navigate?.Invoke(first);
                _nextRepeat = now + RepeatDelay;
            }
        }
        else if (direction is { } held && now >= _nextRepeat)
        {
            Navigate?.Invoke(held);
            _nextRepeat = now + RepeatIntervalFor(now - _heldStartedAt);
        }
    }

    internal static TimeSpan RepeatIntervalFor(TimeSpan heldFor) => heldFor.TotalMilliseconds switch
    {
        >= 1800 => RepeatFast,
        >= 800 => RepeatMedium,
        _ => RepeatNormal
    };

    /// <summary>All connected controllers combined, with sticks folded into the d-pad bits and max trigger pressure read.</summary>
    private ushort ReadButtons(out byte maxLeftTrigger, out byte maxRightTrigger)
    {
        ushort buttons = 0;
        ushort stickBits = 0;
        maxLeftTrigger = 0;
        maxRightTrigger = 0;
        var now = DateTime.UtcNow;

        for (uint i = 0; i < MaxControllers; i++)
        {
            if (!_connected[i] && now < _nextProbe[i])
            {
                continue;
            }

            _connected[i] = Native.XInputGetState(i, out var state) == Native.ErrorSuccess;
            if (!_connected[i])
            {
                _nextProbe[i] = now + DisconnectedRetry;
                continue;
            }

            var pad = state.Gamepad;
            buttons |= pad.Buttons;
            if (pad.LeftTrigger > maxLeftTrigger)
            {
                maxLeftTrigger = pad.LeftTrigger;
            }

            if (pad.RightTrigger > maxRightTrigger)
            {
                maxRightTrigger = pad.RightTrigger;
            }

            var left = StickToDpad(pad.ThumbLX, pad.ThumbLY, _heldStickBit);
            if (left != 0)
            {
                stickBits |= left;
            }
            else if ((pad.Buttons & AllDpad) == 0)
            {
                stickBits |= StickToDpad(pad.ThumbRX, pad.ThumbRY, _heldStickBit);
            }
        }

        _heldStickBit = stickBits;
        return (ushort)(buttons | stickBits);
    }

    /// <summary>Maps an analog stick to at most one d-pad direction along its dominant axis.</summary>
    internal static ushort StickToDpad(short lx, short ly) => StickToDpad(lx, ly, 0);

    /// <summary>
    /// Maps an analog stick with hysteresis and sticky-axis lock when held so slight drift across 45 degrees
    /// does not cancel key repeat or inject unwanted perpendicular steps.
    /// </summary>
    internal static ushort StickToDpad(short lx, short ly, ushort heldBit)
    {
        var absX = Math.Abs((int)lx);
        var absY = Math.Abs((int)ly);

        if (heldBit is DpadLeft or DpadRight && absX > StickRelease && absY < absX * 1.35)
        {
            return lx < 0 ? DpadLeft : DpadRight;
        }

        if (heldBit is DpadUp or DpadDown && absY > StickRelease && absX < absY * 1.35)
        {
            return ly > 0 ? DpadUp : DpadDown;
        }

        if (absX <= StickEngage && absY <= StickEngage)
        {
            return 0;
        }

        return absX > absY
            ? (lx < 0 ? DpadLeft : DpadRight)
            : (ly > 0 ? DpadUp : DpadDown);
    }

    /// <summary>
    /// The held direction wins while it is still pressed: the Ally's d-pad flickers into diagonals (left
    /// briefly reads as left+down), which would otherwise turn a held Left into a Down.
    /// </summary>
    private FocusNavigationDirection? DirectionOf(ushort buttons) =>
        _heldDirection is { } held && (buttons & BitOf(held)) != 0 ? held
        : (buttons & DpadUp) != 0 ? FocusNavigationDirection.Up
        : (buttons & DpadDown) != 0 ? FocusNavigationDirection.Down
        : (buttons & DpadLeft) != 0 ? FocusNavigationDirection.Left
        : (buttons & DpadRight) != 0 ? FocusNavigationDirection.Right
        : null;

    private static ushort BitOf(FocusNavigationDirection direction) => direction switch
    {
        FocusNavigationDirection.Up => DpadUp,
        FocusNavigationDirection.Down => DpadDown,
        FocusNavigationDirection.Left => DpadLeft,
        _ => DpadRight
    };
}
