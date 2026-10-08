using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// One game started from the library, followed until it closes: for its play time, and so whatever was
/// changed for it (its profile, closed background apps) can be put back. Store games are started through
/// their store, so there is no process of ours to follow; the game's own exe runs from its install folder,
/// and that is what is looked for.
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

    private readonly CancellationTokenSource _stop = new();
    private readonly string? _folder;
    private DateTime? _sleptAt;
    private DateTime? _endedAt;
    private TimeSpan _asleep;

    public GameSession(Game game)
    {
        Game = game;
        _folder = ProcessFolder(game);
    }

    public Game Game { get; }

    public DateTime LaunchedAt { get; } = DateTime.UtcNow;

    /// <summary>When the game's process was first seen. Null until then, and for a game that never appeared.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Whether there is a folder to look for the game in at all.</summary>
    public bool CanFollow => _folder is not null;

    /// <summary>The time played so far, not counting time the device spent asleep.</summary>
    public TimeSpan Played
    {
        get
        {
            if (StartedAt is not { } started)
            {
                return TimeSpan.Zero;
            }

            var end = _endedAt ?? DateTime.UtcNow;
            var asleep = _asleep + (_sleptAt is { } slept ? end - slept : TimeSpan.Zero);
            var played = end - started - asleep;
            return played > TimeSpan.Zero ? played : TimeSpan.Zero;
        }
    }

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
            if (_sleptAt is { } slept)
            {
                _asleep += DateTime.UtcNow - slept;
                _sleptAt = null;
            }

            _endedAt = DateTime.UtcNow;
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
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _sleptAt ??= DateTime.UtcNow;
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume && _sleptAt is { } slept)
        {
            _asleep += DateTime.UtcNow - slept;
            _sleptAt = null;
        }
    }
}
