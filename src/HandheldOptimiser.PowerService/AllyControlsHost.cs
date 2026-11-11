using System.Runtime.InteropServices;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Runs the Ally's controller while the app stands in for Armoury Crate: finds it, wakes it, puts it in the
/// saved mode and lighting, and turns the Armoury Crate, Command Center and back paddle buttons into actions.
///
/// One background thread does the finding and the reading. Everything else (the pipe's requests, a resume
/// from sleep, the service stopping) only changes state, sends a mode, or closes the handle so the thread
/// starts again.
/// </summary>
internal sealed partial class AllyControlsHost(Action? toggleOverlay = null) : IDisposable
{
    private const string LauncherExe = "HandheldOptimiser.HomeLauncher.exe";

    // Coming back from sleep, the controller is not always ready straight away.
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(1);

    // A Dispose that stops the thread mid-read returns within this, whether or not the thread has ended.
    private static readonly TimeSpan JoinLimit = TimeSpan.FromMilliseconds(500);

    // Guards the fields below. Never held around a HID call or a launch.
    private readonly object _state = new();

    // Held around each mode sent, so a mode from the pipe and the one applied on reconnect cannot cross,
    // leaving the controller in the older one. Never held by anything that answers the pipe's status.
    private readonly object _send = new();

    // Set by Resume, so a wait between looks for the controller ends early.
    private readonly AutoResetEvent _wake = new(false);

    private readonly AllyButtonFilter _filter = new();
    private readonly string _launcher = Path.Combine(AppContext.BaseDirectory, LauncherExe);

    private CancellationTokenSource? _stop;
    private Thread? _thread;
    private IControllerBackend? _hid;
    private ControllerMode? _mode = AllyControlsStore.SavedMode();
    private string _paddleAction = AllyControlsStore.SavedPaddleAction();
    // Null until the player picks a stick lighting mode, and the lights are not touched till then.
    private string? _lighting = AllyControlsStore.SavedLighting();
    private string _colour = AllyControlsStore.SavedColour();
    private (byte R, byte G, byte B)? _lastBatteryColour;

    // The mode held while Game Bar is open, over the saved one, or null. Never saved.
    private ControllerMode? _hold;
    private DateTime? _resumeAt;
    private bool _disposed;

    /// <summary>Whether the controller is open now.</summary>
    public bool Connected
    {
        get
        {
            lock (_state)
            {
                return _hid is not null;
            }
        }
    }

    /// <summary>The mode the controller is put in, or null when none has been chosen.</summary>
    public ControllerMode? Mode
    {
        get
        {
            lock (_state)
            {
                return _mode;
            }
        }
    }

    /// <summary>What the back paddles (M1 and M2) are set to do.</summary>
    public string PaddleAction
    {
        get
        {
            lock (_state)
            {
                return _paddleAction;
            }
        }
    }

    /// <summary>The thumbstick light ring colour, one of <see cref="AutoTdpProtocol.LightingColours"/>.</summary>
    public string Colour
    {
        get
        {
            lock (_state)
            {
                return _colour;
            }
        }
    }

    /// <summary>The thumbstick light ring mode, or null when the player has not picked one.</summary>
    public string? Lighting
    {
        get
        {
            lock (_state)
            {
                return _lighting;
            }
        }
    }

    /// <summary>Starts the thread that finds and reads the controller, until <paramref name="cancel"/> or Dispose.</summary>
    public void Start(CancellationToken cancel)
    {
        lock (_state)
        {
            if (_disposed || _thread is not null)
            {
                return;
            }

            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _thread = new Thread(() => Run(_stop.Token))
            {
                // A read the handle's close missed can block until the next button press, so the thread
                // must never keep the service's process alive.
                IsBackground = true,
                Name = "Ally controller"
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Back from sleep: closes the controller so the thread opens it again a second later and applies
    /// everything, as the firmware may have forgotten the mode.
    /// </summary>
    public void Resume()
    {
        IControllerBackend? hid;
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _resumeAt = DateTime.UtcNow + ResumeDelay;
            hid = _hid;
            _hid = null;
        }

        Log.Write("Back from sleep, so the controller is opened again");
        hid?.Dispose();
        _wake.Set();
    }

    /// <summary>Saves the mode and sends it to the controller.</summary>
    /// <param name="temporary">
    /// Held but not saved, as while Game Bar is open in Desktop mode, until <see cref="Release"/>. A mode saved
    /// meanwhile, from the widget inside Game Bar, is kept for then: in Desktop mode LB is Tab and RB a click,
    /// which walk Game Bar's lists and close it. Sleep or the controller coming back also ends the hold.
    /// </param>
    /// <returns>A notice for the player when it could not be sent now, or null when it was.</returns>
    public string? SetMode(ControllerMode mode, bool temporary = false)
    {
        if (!temporary)
        {
            AllyControlsStore.SaveMode(mode);
        }

        ControllerMode? hold;
        lock (_state)
        {
            if (temporary)
            {
                _hold = mode;
            }
            else
            {
                _mode = mode;
            }

            hold = _hold;
        }

        Log.Write(temporary ? $"Controller held in {AllyCommands.ModeName(mode)} mode while Game Bar is open"
            : hold is { } held && held != mode ? $"Controller mode set to {AllyCommands.ModeName(mode)}, from when Game Bar closes"
            : $"Controller mode set to {AllyCommands.ModeName(mode)}");
        return SendCurrent();
    }

    /// <summary>Game Bar has closed: the hold ends and the saved mode is put back.</summary>
    /// <returns>A notice for the player when it could not be sent now, or null when it was.</returns>
    public string? Release()
    {
        lock (_state)
        {
            _hold = null;
        }

        Log.Write("Game Bar closed, so the saved controller mode is put back");
        return SendCurrent();
    }

    /// <summary>Saves what the back paddles (M1 and M2) do.</summary>
    /// <returns>Why it was not set, or null.</returns>
    public string? SetPaddleAction(string? action)
    {
        if (!AllyCommands.IsPaddleAction(action))
        {
            return "That paddle action is not supported.";
        }

        var chosen = AllyCommands.ParsePaddleAction(action);
        AllyControlsStore.SavePaddleAction(chosen);
        lock (_state)
        {
            _paddleAction = chosen;
        }

        Log.Write($"Back paddles set to {chosen}");
        return null;
    }

    /// <summary>Saves the thumbstick light ring mode and sends it to the controller.</summary>
    /// <returns>A notice for the player when it could not be sent now, or null when it was.</returns>
    public string? SetLighting(string? lighting)
    {
        if (!AllyCommands.IsLightingMode(lighting))
        {
            return "That stick lighting mode is not supported.";
        }

        var chosen = AllyCommands.ParseLighting(lighting);
        AllyControlsStore.SaveLighting(chosen);
        lock (_state)
        {
            _lighting = chosen;
        }

        Log.Write($"Controller stick lighting set to {chosen}");
        return SendLighting();
    }

    /// <summary>
    /// Saves the thumbstick light ring colour and sends it. Picking a colour while the lights were never set
    /// turns them on at Medium, as the player plainly wants to see it.
    /// </summary>
    /// <returns>A notice for the player when it could not be sent now, or null when it was.</returns>
    public string? SetColour(string? colour)
    {
        if (!AllyCommands.IsLightingColour(colour))
        {
            return "That stick colour is not supported.";
        }

        var chosen = AllyCommands.ParseColour(colour);
        AllyControlsStore.SaveColour(chosen);
        string? turnedOn = null;
        lock (_state)
        {
            _colour = chosen;
            if (_lighting is null)
            {
                _lighting = turnedOn = AutoTdpProtocol.LightingMedium;
            }
        }

        if (turnedOn is not null)
        {
            AllyControlsStore.SaveLighting(turnedOn);
        }

        Log.Write($"Controller stick colour set to {chosen}");
        return SendLighting();
    }

    /// <summary>Sends the saved mode and colour to the controller.</summary>
    private string? SendLighting()
    {
        IControllerBackend? hid;
        string? lighting;
        string colour;
        lock (_state)
        {
            lighting = _lighting;
            colour = _colour;
            _lastBatteryColour = null;
            hid = _hid;
        }

        if (lighting is null)
        {
            return null;
        }

        if (hid is null)
        {
            return "The controller is not connected. It will change when it is.";
        }

        var battery = lighting == AutoTdpProtocol.LightingBattery ? ReadBatteryPercent() : null;
        bool sent;
        lock (_send)
        {
            sent = hid.SetLighting(lighting, battery, colour);
        }

        var chosen = lighting;
        if (sent)
        {
            lock (_state)
            {
                _lastBatteryColour = chosen == AutoTdpProtocol.LightingBattery ? AllyCommands.BatteryColour(battery) : null;
            }

            return null;
        }

        Log.Write("The controller did not take the stick lighting, so it is opened again");
        Drop(hid);
        return "The controller did not answer. It will change when it is found again.";
    }

    /// <summary>Updates the stick ring colour when set to Battery and the charge crosses a colour band.</summary>
    public void RefreshBatteryLighting()
    {
        IControllerBackend? hid;
        (byte R, byte G, byte B)? previous;
        lock (_state)
        {
            if (_lighting != AutoTdpProtocol.LightingBattery || _hid is null)
            {
                return;
            }

            hid = _hid;
            previous = _lastBatteryColour;
        }

        var percent = ReadBatteryPercent();
        var colour = AllyCommands.BatteryColour(percent);
        if (previous == colour)
        {
            return;
        }

        bool sent;
        lock (_send)
        {
            sent = hid.SetLighting(AutoTdpProtocol.LightingBattery, percent, Colour);
        }

        if (sent)
        {
            lock (_state)
            {
                _lastBatteryColour = colour;
            }
        }
    }

    /// <summary>Sends the mode the controller should be in now, the hold or the saved one.</summary>
    private string? SendCurrent()
    {
        IControllerBackend? hid;
        lock (_state)
        {
            hid = _hid;
        }

        if (hid is null)
        {
            return "The controller is not connected. It will change when it is.";
        }

        bool sent;
        lock (_send)
        {
            // Read again under the send lock, so the newest mode is the one sent last.
            ControllerMode? mode;
            lock (_state)
            {
                mode = AllyCommands.ModeToSend(_mode, _hold);
            }

            sent = mode is not { } chosen || hid.SetMode(chosen);
        }

        if (sent)
        {
            return null;
        }

        // A failed write means the controller has gone, so it is closed and found again, which applies the mode.
        Log.Write("The controller did not take the mode, so it is opened again");
        Drop(hid);
        return "The controller did not answer. It will change when it is found again.";
    }

    public void Dispose()
    {
        IControllerBackend? hid;
        Thread? thread;
        lock (_state)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hid = _hid;
            _hid = null;
            thread = _thread;
        }

        _stop?.Cancel();
        hid?.Dispose();

        // Bounded, so stopping the service never waits on a read the close did not reach. The token source and
        // the event are left to the collector, as the thread may still be using them after the limit.
        thread?.Join(JoinLimit);
    }

    /// <summary>The thread: find the controller, set it up, read it until it goes, and again.</summary>
    private void Run(CancellationToken stop)
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!WaitForResume(stop, ref attempt))
                {
                    return;
                }

                if (ControllerBackends.Open() is not { } hid)
                {
                    if (attempt == 0)
                    {
                        Log.Write("Controller not found, looking again");
                    }

                    if (!Wait(AllyCommands.RetryDelay(attempt++), stop))
                    {
                        return;
                    }

                    continue;
                }

                lock (_state)
                {
                    if (_disposed)
                    {
                        hid.Dispose();
                        return;
                    }

                    _hid = hid;
                }

                Log.Write($"Controller found: {hid.Describe()}");
                Apply(hid);
                while (hid.TryRead(out var read))
                {
                    // The back-off starts again only once a read has worked. A controller that opens but
                    // cannot be read would otherwise be opened again flat out.
                    attempt = 0;
                    if (_filter.Press(read, DateTime.UtcNow) is { } button)
                    {
                        Act(button);
                    }
                }

                Drop(hid);
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                bool resuming;
                lock (_state)
                {
                    resuming = _resumeAt is not null;
                }

                // A resume closed it on purpose, and its own second is the wait.
                if (resuming)
                {
                    continue;
                }

                Log.Write("Controller lost, looking again");
                if (!Wait(AllyCommands.RetryDelay(attempt++), stop))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // Anything thrown on this thread would end the whole service, TDP Engine included, so it is
                // logged and the controller looked for again.
                Log.Write($"The controller failed: {ex.Message}");
                IControllerBackend? broken;
                lock (_state)
                {
                    broken = _hid;
                    _hid = null;
                }

                broken?.Dispose();
                if (!Wait(AllyCommands.RetryDelay(attempt++), stop))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Wakes the controller and puts it in the saved mode and lighting.</summary>
    private void Apply(IControllerBackend hid)
    {
        // After sleep or a reconnect Game Bar is not holding anything, and the saved mode is what counts.
        string? lighting;
        string colour;
        lock (_state)
        {
            _hold = null;
            lighting = _lighting;
            colour = _colour;
            _lastBatteryColour = null;
        }

        if (!hid.Wake())
        {
            Log.Write("The controller did not take the wake report");
        }

        var battery = lighting == AutoTdpProtocol.LightingBattery ? ReadBatteryPercent() : null;
        lock (_send)
        {
            if (Mode is { } mode && !hid.SetMode(mode))
            {
                Log.Write($"The controller did not take the {AllyCommands.ModeName(mode)} mode");
            }

            // With no stick lighting mode picked, the lights stay as they were.
            if (lighting is null)
            {
                return;
            }

            if (hid.SetLighting(lighting, battery, colour))
            {
                lock (_state)
                {
                    _lastBatteryColour = lighting == AutoTdpProtocol.LightingBattery ? AllyCommands.BatteryColour(battery) : null;
                }
            }
            else
            {
                Log.Write($"The controller did not take the {lighting} lighting");
            }
        }
    }

    /// <summary>What a button does: library, Game Bar, or the player's chosen back paddle action.</summary>
    private void Act(AllyButton button)
    {
        if (button == AllyButton.Paddle)
        {
            ActPaddle();
            return;
        }

        var (arguments, name, what) = button switch
        {
            // --from-button tells the launcher it was started by this service, so it takes the foreground.
            // The launcher's copy of the word is Program.FromButtonArgument, and the two must be kept in step.
            AllyButton.ArmouryCrate => ("--library --from-button", "Armoury Crate", "the library"),
            AllyButton.CommandCenter => ("--gamebar", "Command Center", "Game Bar"),
            _ => (null, null, null)
        };

        if (arguments is null)
        {
            return;
        }

        if (SessionLauncher.Launch(_launcher, arguments, out _) is { } refused)
        {
            Log.Write($"{name} button: could not open {what} ({refused})");
        }
    }

    private void ActPaddle()
    {
        var action = PaddleAction;
        switch (action)
        {
            case AutoTdpProtocol.PaddleNone:
                return;

            case AutoTdpProtocol.PaddleOverlay:
                toggleOverlay?.Invoke();
                return;

            case AutoTdpProtocol.PaddleMode:
                var next = Mode == ControllerMode.Desktop ? ControllerMode.Gamepad : ControllerMode.Desktop;
                _ = SetMode(next);
                return;

            case AutoTdpProtocol.PaddleKeyboard:
                LaunchFromPaddle("--keyboard", "the touch keyboard");
                return;

            case AutoTdpProtocol.PaddleScreenshot:
                LaunchFromPaddle("--screenshot", "a screenshot");
                return;

            case AutoTdpProtocol.PaddleMute:
                LaunchFromPaddle("--mute", "mute");
                return;
        }
    }

    private void LaunchFromPaddle(string arguments, string what)
    {
        if (SessionLauncher.Launch(_launcher, arguments, out _) is { } refused)
        {
            Log.Write($"Back paddle: could not trigger {what} ({refused})");
        }
    }

    private static int? ReadBatteryPercent() =>
        GetSystemPowerStatus(out var status) && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Lets go of this handle, and forgets it if it is still the current one.</summary>
    private void Drop(IControllerBackend hid)
    {
        lock (_state)
        {
            if (ReferenceEquals(_hid, hid))
            {
                _hid = null;
            }
        }

        hid.Dispose();
    }

    /// <summary>
    /// After a Resume, waits until its second has passed, and starts the back-off again, as the controller is
    /// expected back soon after sleep. False when stopped meanwhile.
    /// </summary>
    private bool WaitForResume(CancellationToken stop, ref int attempt)
    {
        DateTime? until;
        lock (_state)
        {
            until = _resumeAt;
            _resumeAt = null;
        }

        if (until is not null)
        {
            attempt = 0;
        }

        return until is not { } at || at <= DateTime.UtcNow || Wait(at - DateTime.UtcNow, stop, wakeable: false);
    }

    /// <summary>
    /// Waits for <paramref name="delay"/>, or less when Resume asks for the controller again. False when stopped.
    /// </summary>
    private bool Wait(TimeSpan delay, CancellationToken stop, bool wakeable = true)
    {
        if (delay <= TimeSpan.Zero)
        {
            return !stop.IsCancellationRequested;
        }

        if (wakeable)
        {
            WaitHandle.WaitAny([stop.WaitHandle, _wake], delay);
        }
        else
        {
            stop.WaitHandle.WaitOne(delay);
        }

        return !stop.IsCancellationRequested;
    }
}
