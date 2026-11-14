using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The games the library is installing itself, one at a time and the rest waiting, as the spec's "Native
/// installs" asks: shown on their tiles and in the Downloads list, paused while a game is played unless the
/// player says otherwise, and carried on where they stopped after a pause, a lost connection or the library
/// closing. GOG only for now (stage 2); Epic will use the same queue.
/// </summary>
internal sealed class InstallQueue
{
    private enum Status
    {
        Waiting,
        Running,
        Paused,
        PausedForGame,
        Stopped,
    }

    /// <summary>One game in the queue.</summary>
    private sealed class Entry(GogInstaller.Job job)
    {
        public GogInstaller.Job Job { get; } = job;

        public Status Status { get; set; }

        public CancellationTokenSource? Run { get; set; }

        /// <summary>Why it stopped, for a download that will be tried again by itself.</summary>
        public string? Problem { get; set; }

        public DateTime RetryAt { get; set; }

        public string Key => $"gog:{Job.ProductId}";
    }

    /// <summary>The queue as kept between runs of the library.</summary>
    private sealed record Saved(string ProductId, string Title, string? Cover, InstallMode Mode, string? Folder, bool Paused);

    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly LibraryState _state;
    private readonly NoticePresenter _notices;
    private readonly Func<Task> _rescan;
    private readonly Func<bool> _playing;
    private readonly GogInstaller _installer = new(GogInstaller.Downloads);
    private readonly List<Entry> _entries = [];
    private readonly DispatcherTimer _timer;

    // A Cancel or Uninstall pressed once, which a second press within a few seconds carries out.
    private (string Key, string What, DateTime At)? _armed;

    /// <param name="playing">Whether a game is being played now, with the library behind it.</param>
    public InstallQueue(LibraryState state, NoticePresenter notices, Func<Task> rescan, Func<bool> playing, Dispatcher dispatcher)
    {
        _state = state;
        _notices = notices;
        _rescan = rescan;
        _playing = playing;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    private static string QueuePath => Path.Combine(NativeInstalls.Folder, "queue.json");

    /// <summary>Told after each look at the queue, with each install's progress. Empty once there are none.</summary>
    public event Action<IReadOnlyList<DownloadRow>>? Changed;

    /// <summary>Picks up the installs that were under way when the library last closed, and looks for updates.</summary>
    public void Start()
    {
        try
        {
            var saved = File.Exists(QueuePath) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(QueuePath)) ?? [] : [];
            foreach (var item in saved)
            {
                _entries.Add(new Entry(new GogInstaller.Job
                {
                    ProductId = item.ProductId,
                    Title = item.Title,
                    Cover = item.Cover,
                    Mode = item.Mode,
                    Folder = item.Folder,
                })
                { Status = item.Paused ? Status.Paused : Status.Waiting });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Program.Log($"Could not read the install queue: {ex.GetType().Name}");
        }

        if (_entries.Count > 0)
        {
            _timer.Start();
            Tick();
        }

        _ = CheckUpdatesAsync();
    }

    /// <summary>Stops every download as the library closes, keeping how far each got.</summary>
    public void Stop()
    {
        _timer.Stop();
        foreach (var entry in _entries)
        {
            entry.Run?.Cancel();
        }
    }

    /// <summary>Whether a press on a game that is not installed should install it here rather than open its store.</summary>
    public static bool InstallsHere(Game game) =>
        game.Store == GameStore.Gog && !game.Installed && game.Key.StartsWith("gog:", StringComparison.Ordinal)
        && StoreSessions.SignedIn(GameStore.Gog);

    /// <summary>Puts a game in the queue to install, update or repair.</summary>
    public void Add(Game game, InstallMode mode = InstallMode.Install)
    {
        var id = game.Key[4..];
        if (_entries.Any(e => e.Job.ProductId == id))
        {
            _notices.Show($"{game.Title} is already in the downloads.");
            return;
        }

        var installed = NativeInstalls.Find(game.Key);
        _entries.Add(new Entry(new GogInstaller.Job
        {
            ProductId = id,
            Title = game.Title,
            Cover = game.CoverUrl ?? installed?.Cover,
            Mode = mode,
            Folder = installed?.Folder,
        }));
        Program.Log($"Queued {mode.ToString().ToLowerInvariant()} of {game.Key}");
        _notices.Show(mode switch
        {
            InstallMode.Update => $"Updating {game.Title}...",
            InstallMode.Repair => $"Checking {game.Title}'s files...",
            _ when _entries.Count > 1 => $"{game.Title} will download after the others.",
            _ => $"Getting {game.Title} ready to download...",
        });
        Save();
        _timer.Start();
        Tick();
    }

    /// <summary>
    /// The rows the game's own menu shows for its install, or for a game the library installed. Each press
    /// says whether it is done; Cancel and Uninstall are not until pressed a second time.
    /// </summary>
    public IEnumerable<(string Label, string? Value, Func<bool> Press)> ActionsFor(GameTile tile)
    {
        var key = tile.Game.Key;
        if (_entries.FirstOrDefault(e => e.Key == key) is { } entry)
        {
            yield return entry.Status is Status.Paused or Status.Stopped
                ? ("Carry on downloading", Progress(entry).Text, () => Done(() => Resume(key)))
                : ("Pause download", Progress(entry).Text, () => Done(() => Pause(key)));
            yield return ("Cancel download", null, () => Cancel(key));
            yield break;
        }

        if (tile.IsInstalled && NativeInstalls.Find(key) is { } install)
        {
            if (install.UpdateBuildId is not null)
            {
                yield return ("Update", "New version", () => Done(() => Add(tile.Game, InstallMode.Update)));
            }

            yield return ("Check files", null, () => Done(() => Add(tile.Game, InstallMode.Repair)));
            yield return ("Uninstall", CleanResult.Size(install.Size), () => Uninstall(tile));
        }
    }

    private static bool Done(Action action)
    {
        action();
        return true;
    }

    /// <summary>Pauses or carries on a download, from its row in the Downloads list.</summary>
    public void Toggle(string key)
    {
        if (_entries.FirstOrDefault(e => e.Key == key) is { Status: Status.Paused or Status.Stopped })
        {
            Resume(key);
        }
        else
        {
            Pause(key);
        }
    }

    public void Pause(string key)
    {
        if (_entries.FirstOrDefault(e => e.Key == key) is not { } entry)
        {
            return;
        }

        entry.Status = Status.Paused;
        entry.Run?.Cancel();
        _notices.Show($"{entry.Job.Title} is paused.");
        Save();
        Tick();
    }

    public void Resume(string key)
    {
        if (_entries.FirstOrDefault(e => e.Key == key) is not { } entry)
        {
            return;
        }

        entry.Status = Status.Waiting;
        entry.Problem = null;
        _notices.Show($"{entry.Job.Title} will carry on downloading.");
        Save();
        _timer.Start();
        Tick();
    }

    /// <summary>Stops a download and takes away what it fetched. Pressed twice, as Restart is; true once done.</summary>
    public bool Cancel(string key)
    {
        if (_entries.FirstOrDefault(e => e.Key == key) is not { } entry)
        {
            return true;
        }

        if (!Confirmed(key, "cancel"))
        {
            _notices.Show("Press Cancel download again to stop it and delete what it downloaded.");
            return false;
        }

        _entries.Remove(entry);
        entry.Run?.Cancel();
        var job = entry.Job;
        _ = Task.Run(async () =>
        {
            // Let the run see it was cancelled and let go of its files first.
            await Task.Delay(1500);
            if (job.Mode == InstallMode.Install)
            {
                GogInstaller.Discard(job.ProductId, job.Folder);
            }
        });
        Program.Log($"Cancelled the download of {key}");
        _notices.Show($"{job.Title} was cancelled.");
        Save();
        Tick();
        return true;
    }

    private bool Uninstall(GameTile tile)
    {
        if (NativeInstalls.Find(tile.Game.Key) is not { } install)
        {
            return true;
        }

        if (!Confirmed(tile.Game.Key, "uninstall"))
        {
            _notices.Show($"Press Uninstall again to delete {install.Title} and its {CleanResult.Size(install.Size)} of files.");
            return false;
        }

        _notices.Show($"Uninstalling {install.Title}...");
        _ = Task.Run(() =>
        {
            try
            {
                GogInstaller.Uninstall(install);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log($"Could not uninstall {install.Key}: {ex.Message}");
                return ex.Message;
            }
        }).ContinueWith(done =>
        {
            _notices.Show(done.Result is null ? $"{install.Title} is uninstalled." : $"{install.Title} could not be fully deleted: a file is in use.");
            _ = _rescan();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return true;
    }

    private bool Confirmed(string key, string what)
    {
        if (_armed is { } armed && armed.Key == key && armed.What == what && DateTime.UtcNow - armed.At < TimeSpan.FromSeconds(6))
        {
            _armed = null;
            return true;
        }

        _armed = (key, what, DateTime.UtcNow);
        return false;
    }

    // ----- Running -----

    private void Tick()
    {
        var playing = _playing() && !LibrarySettings.DownloadWhilePlaying;
        foreach (var entry in _entries)
        {
            if (playing && entry.Status == Status.Running)
            {
                entry.Status = Status.PausedForGame;
                entry.Run?.Cancel();
                Program.Log($"Paused the download of {entry.Key} while a game is played");
            }
            else if (!playing && entry.Status == Status.PausedForGame)
            {
                entry.Status = Status.Waiting;
            }
            else if (entry.Status == Status.Stopped && DateTime.UtcNow >= entry.RetryAt)
            {
                entry.Status = Status.Waiting;
            }
        }

        // One at a time, in the order they were asked for.
        if (!playing && !_entries.Any(e => e.Status == Status.Running) && _entries.FirstOrDefault(e => e.Status == Status.Waiting) is { } next)
        {
            _ = RunAsync(next);
        }

        Publish();
        if (_entries.Count == 0)
        {
            _timer.Stop();
        }
    }

    private async Task RunAsync(Entry entry)
    {
        entry.Status = Status.Running;
        entry.Problem = null;
        var run = new CancellationTokenSource();
        entry.Run = run;
        var job = entry.Job;
        Program.Log($"Downloading {entry.Key} ({job.Mode.ToString().ToLowerInvariant()})");

        try
        {
            var record = await Task.Run(() => _installer.RunAsync(job, run.Token));
            _entries.Remove(entry);
            Save();
            Program.Log($"Installed {entry.Key}, build {record.BuildId}");
            _notices.Show(job.Mode switch
            {
                InstallMode.Update => $"{job.Title} is up to date.",
                InstallMode.Repair => job.Total == 0 ? $"{job.Title}'s files are all fine." : $"{job.Title}'s files are put right.",
                _ => $"{job.Title} is installed.",
            });
            await _rescan();
        }
        catch (Exception ex) when (run.IsCancellationRequested && ex is OperationCanceledException or GogInstaller.InstallException)
        {
            // Paused, paused for a game, or cancelled: whoever stopped it has set what comes next. Stopped while
            // GOG's lists were being read, the read fails rather than being cancelled, which is no failure: on
            // 2026-11-14 that dropped a download paused for a game.
            Save();
        }
        catch (GogInstaller.InstallException ex)
        {
            _entries.Remove(entry);
            Save();
            Program.Log($"Could not install {entry.Key}: {ex.Message}");
            _notices.Show($"{job.Title} could not be installed. {ex.Message}");
            if (job.Mode == InstallMode.Install && job.Done == 0)
            {
                GogInstaller.Discard(job.ProductId, job.Folder);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            // A lost connection, a full disk or a file in use: tried again in a minute, from where it got to.
            entry.Status = Status.Stopped;
            entry.RetryAt = DateTime.UtcNow + RetryAfter;
            entry.Problem = ex is IOException { HResult: unchecked((int)0x80070070) } ? "The disk is full." : "The connection dropped.";
            Program.Log($"The download of {entry.Key} stopped: {ex.GetType().Name}: {ex.Message}");
            _notices.Show($"{job.Title} stopped downloading. {entry.Problem} It will try again in a minute.");
            Save();
        }
        finally
        {
            if (ReferenceEquals(entry.Run, run))
            {
                entry.Run = null;
            }

            run.Dispose();
            if (entry.Status == Status.Running)
            {
                entry.Status = Status.Waiting;
            }

            _timer.Start();
        }
    }

    // ----- Showing -----

    private static SteamInstallProgress Progress(Entry entry)
    {
        var job = entry.Job;
        return entry.Status switch
        {
            Status.Waiting => new SteamInstallProgress(InstallPhase.Waiting, 0, 0),
            Status.Paused or Status.PausedForGame or Status.Stopped => new SteamInstallProgress(InstallPhase.Paused, job.Done, job.Total),
            _ when job.Checking => new SteamInstallProgress(InstallPhase.Installing, 0, 0),
            _ => new SteamInstallProgress(InstallPhase.Downloading, Math.Min(job.Done, job.Total * 99 / 100), job.Total),
        };
    }

    private void Publish()
    {
        var rows = new List<DownloadRow>();
        foreach (var entry in _entries)
        {
            var progress = Progress(entry);
            rows.Add(new DownloadRow(entry.Key, entry.Job.Title, progress));
            if (_state.AllTiles.FirstOrDefault(t => t.Game.Key == entry.Key) is { } tile)
            {
                tile.ShowInstall(progress.Text, progress.Fraction);
            }
        }

        // Tiles of installs that have just left the queue go back to how they were.
        foreach (var tile in _state.AllTiles.Where(t => t.IsInstalling && t.Game.Store == GameStore.Gog && !_entries.Any(e => e.Key == t.Game.Key)))
        {
            tile.ShowInstall(null, 0);
        }

        Changed?.Invoke(rows);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(NativeInstalls.Folder);
            var saved = _entries.Select(e => new Saved(e.Job.ProductId, e.Job.Title, e.Job.Cover, e.Job.Mode, e.Job.Folder, e.Status == Status.Paused)).ToList();
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not keep the install queue: {ex.Message}");
        }
    }

    // ----- Updates -----

    /// <summary>
    /// Asks GOG for the newest build of each game the library installed, and marks those with a newer one, so
    /// their menu offers Update. GOG games are DRM free and run whatever their version, so nothing is forced.
    /// </summary>
    public async Task CheckUpdatesAsync()
    {
        var installs = NativeInstalls.All().Where(i => i.Store == GameStore.Gog).ToList();
        if (installs.Count == 0 || !StoreSessions.SignedIn(GameStore.Gog))
        {
            return;
        }

        var found = 0;
        foreach (var install in installs)
        {
            if (await GogContent.LatestBuildAsync(install.Id) is { } build)
            {
                var update = build.BuildId != install.BuildId ? build.BuildId : null;
                if (update != install.UpdateBuildId)
                {
                    NativeInstalls.Save(install with { UpdateBuildId = update });
                }

                found += update is null ? 0 : 1;
            }
        }

        if (found > 0)
        {
            Program.Log($"{found} GOG games installed by the library have updates");
        }
    }
}
