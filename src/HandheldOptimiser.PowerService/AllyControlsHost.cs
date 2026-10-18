namespace HandheldOptimiser.PowerService;

/// <summary>
/// Runs the Ally's controller while the app stands in for Armoury Crate: finds it, wakes it, puts it in the
/// saved mode, and turns the Armoury Crate and Command Center buttons into the library and Game Bar.
///
/// One background thread does the finding and the reading. Everything else (the pipe's requests, a resume
/// from sleep, the service stopping) only changes state, sends a mode, or closes the handle so the thread
/// starts again.
/// </summary>
internal sealed class AllyControlsHost : IDisposable
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
    private AllyHid? _hid;
    private ControllerMode? _mode = AllyControlsStore.SavedMode();
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
        AllyHid? hid;
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
    /// <returns>A notice for the player when it could not be sent now, or null when it was.</returns>
    public string? SetMode(ControllerMode mode)
    {
        AllyControlsStore.SaveMode(mode);
        AllyHid? hid;
        lock (_state)
        {
            _mode = mode;
            hid = _hid;
        }

        Log.Write($"Controller mode set to {AllyCommands.ModeName(mode)}");
        if (hid is null)
        {
            return "The controller is not connected. It will change when it is.";
        }

        bool sent;
        lock (_send)
        {
            // Read again under the send lock, so the newest mode is the one sent last.
            sent = hid.Send(AllyCommands.Mode(Mode ?? mode));
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
        AllyHid? hid;
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
                if (!WaitForResume(stop))
                {
                    return;
                }

                if (AllyHid.Open() is not { } hid)
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

                attempt = 0;
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
                while (hid.Read() is { } report)
                {
                    if (_filter.Press(report, DateTime.UtcNow) is { } button)
                    {
                        Act(button);
                    }
                }

                Drop(hid);
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                Log.Write("Controller lost, looking again");
            }
            catch (Exception ex)
            {
                // Anything thrown on this thread would end the whole service, TDP Engine included, so it is
                // logged and the controller looked for again.
                Log.Write($"The controller failed: {ex.Message}");
                AllyHid? broken;
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

    /// <summary>Wakes the controller and puts it in the saved mode, if one is saved.</summary>
    private void Apply(AllyHid hid)
    {
        if (!hid.Send(AllyCommands.Wake()))
        {
            Log.Write("The controller did not take the wake report");
        }

        lock (_send)
        {
            if (Mode is { } mode && !hid.Send(AllyCommands.Mode(mode)))
            {
                Log.Write($"The controller did not take the {AllyCommands.ModeName(mode)} mode");
            }
        }
    }

    /// <summary>What a button does. The paddles do nothing here: their mapping lives on the controller.</summary>
    private void Act(AllyButton button)
    {
        var (arguments, name, what) = button switch
        {
            AllyButton.ArmouryCrate => ("--library", "Armoury Crate", "the library"),
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

    /// <summary>Lets go of this handle, and forgets it if it is still the current one.</summary>
    private void Drop(AllyHid hid)
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

    /// <summary>After a Resume, waits until its second has passed. False when stopped meanwhile.</summary>
    private bool WaitForResume(CancellationToken stop)
    {
        DateTime? until;
        lock (_state)
        {
            until = _resumeAt;
            _resumeAt = null;
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
