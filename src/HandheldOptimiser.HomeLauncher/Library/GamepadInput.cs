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
    Menu,
    View
}

/// <summary>
/// Polls XInput controllers while the library is in front, so the d-pad or left stick moves between
/// tiles, A launches, X opens a game's options, Y opens quick settings and the bumpers change the store
/// filter. Polling stops whenever the library loses focus: the game owns the controller then, and the
/// library must not react to in-game presses.
/// </summary>
internal sealed class GamepadInput
{
    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;

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

    private const short StickThreshold = 16000;
    private const uint MaxControllers = 4;

    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

    // XInputGetState is slow for empty slots, so those are only retried now and then.
    private static readonly TimeSpan DisconnectedRetry = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private readonly DateTime[] _nextProbe = new DateTime[MaxControllers];
    private readonly bool[] _connected = new bool[MaxControllers];

    private ushort _previous;
    private FocusNavigationDirection? _heldDirection;
    private DateTime _nextRepeat;

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
        _previous = ReadButtons();
        _heldDirection = null;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        var buttons = ReadButtons();
        var pressed = (ushort)(buttons & ~_previous);
        _previous = buttons;

        foreach (var (bit, action) in Buttons)
        {
            if ((pressed & bit) != 0)
            {
                Pressed?.Invoke(action);
            }
        }

        var direction = DirectionOf(buttons);
        var now = DateTime.UtcNow;

        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            if (direction is { } first)
            {
                Navigate?.Invoke(first);
                _nextRepeat = now + RepeatDelay;
            }
        }
        else if (direction is { } held && now >= _nextRepeat)
        {
            Navigate?.Invoke(held);
            _nextRepeat = now + RepeatInterval;
        }
    }

    /// <summary>All connected controllers combined, with the left stick folded into the d-pad bits.</summary>
    private ushort ReadButtons()
    {
        ushort buttons = 0;
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
            if (pad.ThumbLY > StickThreshold) buttons |= DpadUp;
            if (pad.ThumbLY < -StickThreshold) buttons |= DpadDown;
            if (pad.ThumbLX < -StickThreshold) buttons |= DpadLeft;
            if (pad.ThumbLX > StickThreshold) buttons |= DpadRight;
        }

        return buttons;
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
