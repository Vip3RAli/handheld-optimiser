using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// One game started from the library, followed until it closes: for its play time, and so whatever was
/// changed for it (its profile, closed background apps) can be put back. Store games are started through
/// their store, so there is no process of ours to follow; the game's own exe runs from its install folder,
/// and that is what is looked for. For Quick Resume it can also pause those processes and resume them.
/// </summary>
internal sealed class GameSession
{
    // How long a game may take to appear. A store client that updates first can take a while.
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FindEvery = TimeSpan.FromSeconds(3);

    // A launcher that closes as the game opens, or a game that restarts itself, leaves a short gap.
    private static readonly TimeSpan Handover = TimeSpan.FromSeconds(10);

    // When a process cannot be waited on, how often to look again instead.
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);

    // How long a game asked to close may take before its processes are ended.
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly string? _folder;
    private readonly Lazy<string?> _antiCheat;
    private DateTime? _endedAt;

    // The paused processes and the time not played, guarded by _gate: pausing runs off the UI thread,
    // and Windows reports sleep on a thread of its own.
    private readonly object _gate = new();
    private List<PausedProcess> _paused = [];
    private bool _asleepNow;
    private DateTime? _idleSince;
    private TimeSpan _idle;

    public GameSession(Game game)
    {
        Game = game;
        _folder = ProcessFolder(game);
        _antiCheat = new Lazy<string?>(() => _folder is null ? null : GamePause.FindAntiCheat(_folder));
    }

    public Game Game { get; }

    public DateTime LaunchedAt { get; } = DateTime.UtcNow;

    /// <summary>When the game's process was first seen. Null until then, and for a game that never appeared.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Whether there is a folder to look for the game in at all.</summary>
    public bool CanFollow => _folder is not null;

    /// <summary>The time played so far, not counting time the device spent asleep or the game was paused.</summary>
    public TimeSpan Played
    {
        get
        {
            if (StartedAt is not { } started)
            {
                return TimeSpan.Zero;
            }

            lock (_gate)
            {
                var end = _endedAt ?? DateTime.UtcNow;
                var idle = _idle + (_idleSince is { } since && end > since ? end - since : TimeSpan.Zero);
                var played = end - started - idle;
                return played > TimeSpan.Zero ? played : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Whether the game is paused by Quick Resume.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused.Count > 0;
            }
        }
    }

    /// <summary>The anti-cheat in the game's folder, which keeps it from being paused, or null. Looking takes a moment.</summary>
    public string? AntiCheat => _antiCheat.Value;

    /// <summary>
    /// The folder the game's processes run from. An emulator game runs in its emulator, so that is the
    /// emulator's folder, not the folder the game file is in.
    /// </summary>
    public static string? ProcessFolder(Game game)
    {
        var folder = game.Store == GameStore.Emulator
            ? Path.GetDirectoryName(game.ExecutablePath)
            : game.InstallDirectory ?? Path.GetDirectoryName(game.ExecutablePath);

        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        try
        {
            return Directory.Exists(folder) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The game's main window, to put back in front after the device wakes, or 0 when none of its
    /// processes has one. Call it off the UI thread.
    /// </summary>
    public nint MainWindow()
    {
        if (_folder is null)
        {
            return 0;
        }

        var running = Find(_folder);
        try
        {
            foreach (var process in running)
            {
                try
                {
                    if (process.MainWindowHandle != 0)
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Closed since it was found.
                }
            }

            return 0;
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Pauses every process of the game, all or none: a game half paused is worse than one running. Call it
    /// off the UI thread.
    /// </summary>
    public PauseOutcome Pause()
    {
        if (_folder is null || GamePause.TooBroad(_folder))
        {
            return PauseOutcome.CannotFollow;
        }

        if (AntiCheat is not null)
        {
            return PauseOutcome.AntiCheat;
        }

        lock (_gate)
        {
            if (_paused.Count > 0)
            {
                return PauseOutcome.Paused;
            }

            var running = Find(_folder);
            var opened = new List<PausedProcess>();
            try
            {
                // A game that is closing has no window left, and pausing it would stop it closing.
                if (!running.Any(HasWindow))
                {
                    return PauseOutcome.NotRunning;
                }

                foreach (var process in running)
                {
                    var handle = Native.OpenForPause(process.Id);
                    if (handle == 0)
                    {
                        Program.Log($"{Game.Key} cannot be paused: Windows refused process {process.Id}");
                        return PauseOutcome.Refused;
                    }

                    opened.Add(new PausedProcess(process.Id, handle, Native.ProcessStarted(handle)));
                }

                var paused = new List<PausedProcess>();
                foreach (var process in opened)
                {
                    if (!Native.SuspendProcess(process.Handle))
                    {
                        Program.Log($"{Game.Key} cannot be paused: process {process.Id} would not pause");
                        foreach (var done in paused)
                        {
                            Native.ResumeProcess(done.Handle);
                        }

                        return PauseOutcome.Refused;
                    }

                    paused.Add(process);
                }

                // The handles stay open while it is paused, so no other process can be given these ids.
                _paused = opened;
                opened = [];
                UpdateIdle();
                GamePause.Remember(_paused.Select(p => (p.Id, p.Started)));
                Program.Log($"{Game.Key} paused ({_paused.Count} processes)");
                return PauseOutcome.Paused;
            }
            finally
            {
                foreach (var process in opened)
                {
                    Native.CloseProcess(process.Handle);
                }

                foreach (var process in running)
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Resumes the game if it is paused. Quick enough for the UI thread.</summary>
    /// <returns>Whether it was paused.</returns>
    public bool Resume()
    {
        lock (_gate)
        {
            if (_paused.Count == 0)
            {
                return false;
            }

            foreach (var process in _paused)
            {
                // One that has been ended meanwhile simply cannot be resumed.
                Native.ResumeProcess(process.Handle);
                Native.CloseProcess(process.Handle);
            }

            _paused = [];
            UpdateIdle();
            GamePause.Forget();
            Program.Log($"{Game.Key} resumed");
            return true;
        }
    }

    /// <summary>Whether the process is one of the game's paused ones.</summary>
    public bool IsPausedProcess(int processId)
    {
        lock (_gate)
        {
            return _paused.Exists(p => p.Id == processId);
        }
    }

    /// <summary>
    /// Closes the game: resumed if paused, asked to close as its own close button would, and ended after
    /// <paramref name="wait"/> if it is still running. Call it off the UI thread.
    /// </summary>
    public void Close(TimeSpan wait)
    {
        Resume();
        if (_folder is null || GamePause.TooBroad(_folder))
        {
            return;
        }

        var running = Find(_folder);
        try
        {
            foreach (var process in running)
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // Closed already.
                }
            }

            var until = DateTime.UtcNow + wait;
            foreach (var process in running)
            {
                WaitForExit(process, until - DateTime.UtcNow);
            }

            foreach (var process in running)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        WaitForExit(process, KillWait);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    Program.Log($"{Game.Key}: process {process.Id} could not be ended: {ex.Message}");
                }
            }

            Program.Log($"{Game.Key} closed from the library");
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    private static void WaitForExit(Process process, TimeSpan wait)
    {
        try
        {
            if (wait > TimeSpan.Zero)
            {
                process.WaitForExit(wait);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // One this account may not wait on; it is ended or left as it is next.
        }
    }

    private static bool HasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Starts or ends a stretch of time not played: while the device sleeps or the game is paused. Hold _gate.</summary>
    private void UpdateIdle()
    {
        var idle = _asleepNow || _paused.Count > 0;
        if (idle && _idleSince is null)
        {
            _idleSince = DateTime.UtcNow;
        }
        else if (!idle && _idleSince is { } since)
        {
            _idle += DateTime.UtcNow - since;
            _idleSince = null;
        }
    }

    /// <summary>Stops following the game, for when another game starts or the library closes.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Completes once the game has closed, never appeared, or <see cref="Stop"/> was called.</summary>
    public async Task FollowAsync()
    {
        if (_folder is null)
        {
            return;
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        try
        {
            await FollowFolderAsync(_folder, _stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped: whatever was played so far still counts.
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            lock (_gate)
            {
                _endedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task FollowFolderAsync(string folder, CancellationToken cancel)
    {
        while (true)
        {
            var running = await Task.Run(() => Find(folder), cancel);
            try
            {
                if (running.Count == 0)
                {
                    if (StartedAt is null)
                    {
                        if (DateTime.UtcNow - LaunchedAt > StartGrace)
                        {
                            Program.Log($"{Game.Key} was not seen running, so it is not being followed");
                            return;
                        }

                        await Task.Delay(FindEvery, cancel);
                        continue;
                    }

                    await Task.Delay(Handover, cancel);
                    if (!await Task.Run(() => Any(folder), cancel))
                    {
                        return;
                    }

                    continue;
                }

                if (StartedAt is null)
                {
                    StartedAt = DateTime.UtcNow;
                    Program.Log($"{Game.Key} is running");

                    // Looked for now, so the first press of the home button does not wait for it.
                    _ = Task.Run(() => AntiCheat);
                }

                await WaitForAnyExitAsync(running, cancel);
            }
            finally
            {
                foreach (var process in running)
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Waits until one of the processes ends, then the caller looks again for the rest.</summary>
    private static async Task WaitForAnyExitAsync(List<Process> processes, CancellationToken cancel)
    {
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            await Task.WhenAny(processes.Select(p => p.WaitForExitAsync(round.Token)));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // A process this account may not wait on, as some anti-cheat protected games are. Look again later.
            await Task.Delay(PollEvery, cancel);
        }
        finally
        {
            round.Cancel();
        }

        cancel.ThrowIfCancellationRequested();
    }

    private static bool Any(string folder)
    {
        var running = Find(folder);
        foreach (var process in running)
        {
            process.Dispose();
        }

        return running.Count > 0;
    }

    /// <summary>Every process whose exe is inside the folder. The caller disposes them.</summary>
    private static List<Process> Find(string folder)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (Native.ProcessPath(process.Id) is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return found;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (Microsoft.Win32.PowerModes.Suspend or Microsoft.Win32.PowerModes.Resume))
        {
            return;
        }

        lock (_gate)
        {
            _asleepNow = e.Mode == Microsoft.Win32.PowerModes.Suspend;
            UpdateIdle();
        }
    }

    /// <param name="Started">When it started, for the note that lets a later library resume it.</param>
    private sealed record PausedProcess(int Id, nint Handle, long Started);
}

/// <summary>What came of trying to pause a game.</summary>
internal enum PauseOutcome
{
    Paused,

    /// <summary>No window of the game is open: it has closed or is closing.</summary>
    NotRunning,

    /// <summary>It has anti-cheat, which pausing could trip.</summary>
    AntiCheat,

    /// <summary>Windows would not let one of its processes be paused, as for a game running as administrator.</summary>
    Refused,

    /// <summary>There is no folder to find it by, or the folder is too broad to pause everything in it.</summary>
    CannotFollow
}
