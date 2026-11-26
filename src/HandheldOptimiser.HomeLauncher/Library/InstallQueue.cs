using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The games the library is installing itself, one at a time and the rest waiting, as the spec's "Native
/// installs" asks: shown on their tiles and in the Downloads list, paused while a game is played unless the
/// player says otherwise, and carried on where they stopped after a pause, a lost connection or the library
/// closing. GOG games (stage 2) and Epic games (stage 3b) share it.
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
        Cancelling,
    }

    /// <summary>One game in the queue.</summary>
    private sealed class Entry(InstallJob job)
    {
        public InstallJob Job { get; } = job;

        public Status Status { get; set; }

        public CancellationTokenSource? Run { get; set; }

        /// <summary>Why it stopped, for a download that will be tried again by itself.</summary>
        public string? Problem { get; set; }

        public DateTime RetryAt { get; set; }

        /// <summary>The player has been told it stopped, so the tries that follow fail quietly until one gets further.</summary>
        public bool Told { get; set; }

        public string Key => $"{Job.Store.ToString().ToLowerInvariant()}:{Job.ProductId}";
    }

    /// <summary>The queue as kept between runs of the library.</summary>
    private sealed record Saved(string ProductId, string Title, string? Cover, InstallMode Mode, string? Folder, bool Paused,
        GameStore? Store = null, string? Namespace = null, string? CatalogItemId = null);

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

    /// <summary>Whether a game is running, paused or not, when the Epic launcher is not closed under it.</summary>
    public Func<bool>? GameRunning { get; init; }

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
                _entries.Add(new Entry(new InstallJob
                {
                    // A queue kept before Epic games were installed here names no store: those are GOG's.
                    Store = item.Store ?? GameStore.Gog,
                    ProductId = item.ProductId,
                    Title = item.Title,
                    Cover = item.Cover,
                    Mode = item.Mode,
                    Folder = item.Folder,
                    Namespace = item.Namespace,
                    CatalogItemId = item.CatalogItemId,
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
        !game.Installed && game.Store switch
        {
            GameStore.Gog => game.Key.StartsWith("gog:", StringComparison.Ordinal) && StoreSessions.SignedIn(GameStore.Gog),

            // An Epic game has to be in the account's own list, which is where its ids come from.
            GameStore.Epic => game.Key.StartsWith("epic:", StringComparison.Ordinal) && StoreSessions.SignedIn(GameStore.Epic)
                && EpicAccountApi.Find(game.Key[5..]) is not null,
            _ => false,
        };

    /// <summary>Puts a game in the queue to install, update or repair.</summary>
    public void Add(Game game, InstallMode mode = InstallMode.Install)
    {
        var id = game.Key[(game.Key.IndexOf(':') + 1)..];
        if (_entries.Any(e => e.Key == game.Key))
        {
            _notices.Show($"{game.Title} is already in the downloads.");
            return;
        }

        var installed = NativeInstalls.Find(game.Key);
        var epic = game.Store == GameStore.Epic ? EpicAccountApi.Find(id) : null;
        _entries.Add(new Entry(new InstallJob
        {
            Store = game.Store,
            ProductId = id,
            Title = game.Title,
            Cover = game.CoverUrl ?? installed?.Cover,
            Mode = mode,
            Folder = installed?.Folder,
            Namespace = installed?.Namespace ?? epic?.Namespace,
            CatalogItemId = installed?.CatalogItemId ?? epic?.CatalogItemId,
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
            yield break;
        }

        // An Epic game the library would install itself can still be handed to Epic's own launcher, for a
        // game that needs the launcher's anti-cheat set-up or its overlay.
        if (tile.Game.Store == GameStore.Epic && InstallsHere(tile.Game))
        {
            yield return ("Install with the Epic launcher", null, () => Done(() =>
            {
                _notices.Show($"Opening Epic Games to install {tile.Title}...");
                _ = EpicLauncherLog.InstallAsync(tile.Game, _notices, restart: GameRunning?.Invoke() != true);
            }));
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
        entry.Told = false;
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

        var job = entry.Job;
        if (entry.Run is { } run)
        {
            // Keep the entry in the queue while RunAsync winds down and closes open file handles, then discard.
            entry.Status = Status.Cancelling;
            run.Cancel();
        }
        else
        {
            _entries.Remove(entry);
            _ = Task.Run(() =>
            {
                if (job.Mode == InstallMode.Install)
                {
                    NativeInstalls.Discard(job.Store, job.ProductId, job.Folder);
                }
            });
            Save();
        }

        Program.Log($"Cancelled the download of {key}");
        _notices.Show($"{job.Title} was cancelled.");
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
                var kept = NativeInstalls.Uninstall(install);
                Program.Log($"Uninstalled {install.Key}{(kept ? ", keeping what was not GOG's in its folder" : string.Empty)}");
                return kept ? $"{install.Title} is uninstalled. Your saves and settings in its folder were kept." : $"{install.Title} is uninstalled.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log($"Could not uninstall {install.Key}: {ex.Message}");
                return $"{install.Title} could not be fully deleted: a file is in use.";
            }
        }).ContinueWith(done =>
        {
            _notices.Show(done.Result);
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

        // One at a time, in the order they were asked for. A run that was stopped holds its files until it has
        // wound up, so nothing starts while one still is: paused and carried on at once, the same game was
        // otherwise run twice over the same files.
        if (!playing && !_entries.Any(e => e.Status == Status.Running || e.Run is not null) && _entries.FirstOrDefault(e => e.Status == Status.Waiting) is { } next)
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
        var doneBefore = Interlocked.Read(ref job.Done);
        Program.Log($"Downloading {entry.Key} ({job.Mode.ToString().ToLowerInvariant()})");

        try
        {
            var record = await Task.Run(() => job.Store == GameStore.Epic ? EpicInstaller.RunAsync(job, run.Token) : _installer.RunAsync(job, run.Token));
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
        catch (Exception ex) when (run.IsCancellationRequested
            && ex is OperationCanceledException or InstallException or HttpRequestException or IOException or InvalidDataException)
        {
            // Paused, paused for a game, or cancelled: whoever stopped it has set what comes next. Stopped while
            // GOG's lists were being read, the read fails rather than being cancelled, which is no failure: on
            // 2026-11-14 that dropped a download paused for a game. A piece failing as it is stopped is the
            // same, and must not turn a pause into a try again in a minute.
            Save();
        }
        catch (InstallException ex)
        {
            _entries.Remove(entry);
            Save();
            Program.Log($"Could not install {entry.Key}: {ex.Message}");
            _notices.Show($"{job.Title} could not be installed. {ex.Message}");
            if (job.Mode == InstallMode.Install && job.Done == 0)
            {
                NativeInstalls.Discard(job.Store, job.ProductId, job.Folder);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            // A lost connection, a full disk or a file in use: tried again in a minute, from where it got to.
            entry.Status = Status.Stopped;
            entry.RetryAt = DateTime.UtcNow + RetryAfter;
            entry.Problem = ex is IOException { HResult: unchecked((int)0x80070070) } ? "The disk is full." : "The connection dropped.";
            Program.Log($"The download of {entry.Key} stopped: {ex.GetType().Name}: {ex.Message}");

            // Said once, not every minute for as long as the connection is down.
            if (!entry.Told || Interlocked.Read(ref job.Done) > doneBefore)
            {
                _notices.Show($"{job.Title} stopped downloading. {entry.Problem} It will keep trying.");
                entry.Told = true;
            }

            Save();
        }
        catch (Exception ex) when (!LibraryApp.IsFatal(ex))
        {
            // Never fall through to finally with Status.Running, which would reset to Waiting and loop every second.
            entry.Status = Status.Stopped;
            entry.RetryAt = DateTime.UtcNow + RetryAfter;
            entry.Problem = "The download ran into an error.";
            Program.Log($"The download of {entry.Key} failed unexpectedly: {ex}");
            if (!entry.Told)
            {
                _notices.Show($"{job.Title} stopped downloading. {entry.Problem}");
                entry.Told = true;
            }

            Save();
        }
        finally
        {
            if (ReferenceEquals(entry.Run, run))
            {
                entry.Run = null;
            }

            run.Dispose();
            if (entry.Status == Status.Cancelling)
            {
                _entries.Remove(entry);
                Save();
                if (job.Mode == InstallMode.Install)
                {
                    _ = Task.Run(() => NativeInstalls.Discard(job.Store, job.ProductId, job.Folder));
                }
            }
            else if (entry.Status == Status.Running)
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
        foreach (var tile in _state.AllTiles.Where(t => t.IsInstalling && t.Game.Store is GameStore.Gog or GameStore.Epic && !_entries.Any(e => e.Key == t.Game.Key)))
        {
            tile.ShowInstall(null, 0);
        }

        Changed?.Invoke(rows);
    }

    private void Save()
    {
        var temp = $"{QueuePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(NativeInstalls.Folder);
            var saved = _entries
                .Where(e => e.Status != Status.Cancelling)
                .Select(e => new Saved(e.Job.ProductId, e.Job.Title, e.Job.Cover, e.Job.Mode, e.Job.Folder, e.Status == Status.Paused,
                    e.Job.Store, e.Job.Namespace, e.Job.CatalogItemId))
                .ToList();
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, QueuePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { }
            Program.Log($"Could not keep the install queue: {ex.Message}");
        }
    }

    // ----- Updates -----

    /// <summary>
    /// Asks GOG and Epic for the newest build of each game the library installed, and marks those with a
    /// newer one, so their menu offers Update. Nothing is forced: GOG games are DRM free and run whatever
    /// their version, and an online Epic game says so itself when it is behind.
    /// </summary>
    public async Task CheckUpdatesAsync()
    {
        await CheckEpicUpdatesAsync();
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
                    NativeInstalls.SetUpdateBuildId(install.Key, install.BuildId, update);
                }

                found += update is null ? 0 : 1;
            }
        }

        if (found > 0)
        {
            Program.Log($"{found} GOG games installed by the library have updates");
        }
    }

    private static async Task CheckEpicUpdatesAsync()
    {
        var installs = NativeInstalls.All().Where(i => i is { Store: GameStore.Epic, Namespace: not null, CatalogItemId: not null }).ToList();
        if (installs.Count == 0 || await EpicAccountApi.FreshAsync() is not { AccessToken: { } access })
        {
            return;
        }

        var found = 0;
        foreach (var install in installs)
        {
            if (await EpicContent.LatestVersionAsync(access, install.Namespace!, install.CatalogItemId!, install.Id) is { } version)
            {
                var update = version != install.BuildId ? version : null;
                if (update != install.UpdateBuildId)
                {
                    NativeInstalls.SetUpdateBuildId(install.Key, install.BuildId, update);
                }

                found += update is null ? 0 : 1;
            }
        }

        if (found > 0)
        {
            Program.Log($"{found} Epic games installed by the library have updates");
        }
    }
}
