namespace HandheldOptimiser.PowerService;

/// <summary>
/// Radeon Super Resolution and AMD Fluid Motion Frames for the widget. The service never loads AMD's ADLX itself:
/// asked about these from the service's session, ADLX crashed the process (tried on an Ally on 2026-10-28), and in
/// this process that would take the fans and the power limit down with it. Instead the launcher is run as the
/// player with --radeon, which reads or switches them and says what it found through its exit code (see
/// <see cref="RadeonState"/>). Each run takes about a fifth of a second.
/// </summary>
internal sealed class RadeonHost
{
    private const string LauncherExe = "HandheldOptimiser.HomeLauncher.exe";

    // Longer than a run ever takes, so a stuck driver does not hold the widget's answer for long.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Read again at most this often while answers are asked for, so a change made in Radeon Software or another
    // app shows in the widget, without starting a program every couple of seconds while the widget is open.
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(30);

    private readonly string _launcher = Path.Combine(AppContext.BaseDirectory, LauncherExe);
    private readonly object _gate = new();
    private RadeonState _state = new(null, null);
    private DateTime _readAt = DateTime.MinValue;
    private bool _reading;

    /// <summary>
    /// The status with RSR and AFMF as last read, null for each until a read has found it. A read due is started
    /// in the background, so the answer is never held up for it: its result shows in the next answer.
    /// </summary>
    public AutoTdpStatus AddTo(AutoTdpStatus status)
    {
        RadeonState state;
        bool read;
        lock (_gate)
        {
            state = _state;
            read = !_reading && DateTime.UtcNow - _readAt > ReadEvery;
            if (read)
            {
                _reading = true;
            }
        }

        if (read)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Run(RadeonState.Argument);
                }
                finally
                {
                    lock (_gate)
                    {
                        _reading = false;
                    }
                }
            });
        }

        return status with { Rsr = state.Rsr, Afmf = state.Afmf };
    }

    /// <summary>Switches RSR or AFMF, waiting for the result.</summary>
    /// <returns>Why it was not switched, in plain words, or null.</returns>
    public string? Set(string? feature, bool? on)
    {
        if (feature is not (RadeonState.RsrName or RadeonState.AfmfName) || on is not { } value)
        {
            return "The request did not say which setting to change, or whether on or off.";
        }

        Log.Write($"Radeon settings: {feature} {(value ? "on" : "off")}, from the widget");
        return Run($"{RadeonState.Argument} {feature} {(value ? RadeonState.OnName : RadeonState.OffName)}");
    }

    /// <summary>Runs the launcher as the player and keeps what it found.</summary>
    /// <returns>Why nothing was found, or null.</returns>
    private string? Run(string arguments)
    {
        var problem = SessionLauncher.Run(_launcher, arguments, Timeout, out var code);
        var state = code is { } exit ? RadeonState.FromExitCode(exit) : null;
        lock (_gate)
        {
            _readAt = DateTime.UtcNow;
            if (state is { } found)
            {
                _state = found;
            }
        }

        if (problem is not null)
        {
            Log.Write($"Radeon settings: {problem}");
            return "Radeon settings could not be reached. " + problem;
        }

        return state is null ? "Radeon settings could not be read. The graphics driver may not offer them." : null;
    }
}
