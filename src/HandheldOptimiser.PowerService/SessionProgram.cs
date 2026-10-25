namespace HandheldOptimiser.PowerService;

/// <summary>
/// One of the launcher's helper programs that the service keeps running on the player's screen while it is
/// switched on: the performance overlay or the stats screen. It is started with an argument, holds a mutex
/// in the player's session while it runs, and ends itself when it is switched off or the service stops
/// answering. Only the launcher beside this service is ever started.
/// </summary>
internal sealed class SessionProgram(string name, string argument, string mutexName, bool uiAccess)
{
    private const string LauncherExe = "HandheldOptimiser.HomeLauncher.exe";

    // A start that fails, or a program that will not stay, is tried again no sooner than this.
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private readonly string _launcher = Path.Combine(AppContext.BaseDirectory, LauncherExe);
    private DateTime _lastTry = DateTime.MinValue;

    // What the last start said, so each change is logged once. Empty at first, so the first start is logged too.
    private string? _lastProblem = string.Empty;

    /// <summary>Lets the next <see cref="Keep"/> start it at once, as after the player switches it on.</summary>
    public void TryNow() => _lastTry = DateTime.MinValue;

    /// <summary>Each second while it is wanted: starts it when it is not running.</summary>
    public void Keep()
    {
        if (DateTime.UtcNow - _lastTry < RetryEvery || Running())
        {
            return;
        }

        _lastTry = DateTime.UtcNow;
        var problem = SessionLauncher.Launch(_launcher, argument, out _, uiAccess);

        // Said once, not every ten seconds while the screen is locked.
        if (problem != _lastProblem)
        {
            Log.Write(problem is null ? $"{name} started" : $"Could not start the {name.ToLowerInvariant()}: {problem}");
            _lastProblem = problem;
        }
    }

    /// <summary>Whether it holds its mutex in the player's session.</summary>
    private bool Running()
    {
        if (SessionLauncher.ActiveSession() is not { } session)
        {
            return false;
        }

        try
        {
            if (!Mutex.TryOpenExisting($@"Session\{session}\{mutexName}", out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, which is all that is asked.
            return true;
        }
    }
}
