using System.IO;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A game Steam is downloading, as the Downloads list and the status pill show it.</summary>
internal sealed record DownloadRow(string AppId, string Title, SteamInstallProgress Progress);

/// <summary>
/// Follows Steam installs, so a game started from Ready to install shows how far it has got on its tile
/// and moves to the installed games by itself when it is done.
///
/// Steam writes a game's <c>appmanifest_&lt;app&gt;.acf</c> as soon as its install is confirmed, and again
/// when it pauses or finishes, so the library watches Steam's library folders for those files. Steam does
/// not write the byte counts while it downloads, so the progress in between is what the Steam process has
/// written to disk since the manifest was last written, which Windows counts as it goes. Steam's own flag
/// in the registry says which game it is working on. Nothing is asked of Steam and no sign-in is needed.
/// An install started in Steam itself is followed the same way.
/// </summary>
internal sealed class SteamInstallWatcher(LibraryState state, NoticePresenter notices, Func<Task> rescan, Dispatcher dispatcher)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>A followed install: its manifest, and Steam's write count when the manifest was last written.</summary>
    private sealed class Followed(string path)
    {
        public string Path { get; set; } = path;

        /// <summary>The Steam process and its bytes written then, or null when not known.</summary>
        public (int ProcessId, long Written)? Baseline { get; set; }

        /// <summary>What was shown last, for a look that finds the manifest being written.</summary>
        public DownloadRow? LastRow { get; set; }
    }

    // The installs being followed, by app id.
    private readonly Dictionary<string, Followed> _following = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _timer;

    /// <summary>Starts watching Steam's library folders, and picks up installs already under way.</summary>
    public void Start()
    {
        if (_watchers.Count > 0 || SteamLibrary.SteamDirectory() is not { } steamDir)
        {
            return;
        }

        var folders = new List<string>();
        foreach (var library in SteamLibrary.LibraryFolders(steamDir))
        {
            var apps = Path.Combine(library, "steamapps");
            try
            {
                var watcher = new FileSystemWatcher(apps, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                watcher.Created += OnManifestChanged;
                watcher.Changed += OnManifestChanged;
                watcher.Renamed += OnManifestChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                folders.Add(apps);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // A library folder on a drive that is not plugged in: nothing to follow there.
                Program.Log($"Not watching Steam installs in {apps}: {ex.Message}");
            }
        }

        // Installs already under way as the library opened: started in Steam, or before a restart. How much
        // Steam had written when their manifests were written is not known, so they show no figure until
        // Steam writes the manifest again.
        _ = Task.Run(() => UnderWay(folders)).ContinueWith(found =>
        {
            foreach (var (appId, path) in found.Result)
            {
                Follow(appId, path, justWritten: false);
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Stops watching, as the library closes.</summary>
    public void Stop()
    {
        _timer?.Stop();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
        _following.Clear();
    }

    private static List<(string AppId, string Path)> UnderWay(List<string> folders)
    {
        var found = new List<(string, string)>();
        foreach (var folder in folders)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "appmanifest_*.acf"))
                {
                    if (SteamInstallProgress.AppIdFromFileName(Path.GetFileName(path)) is { } appId
                        && ReadManifest(path) is { } text
                        && SteamInstallProgress.Read(text) is { Phase: not InstallPhase.Installed })
                    {
                        found.Add((appId, path));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder went away; its installs are followed if it comes back and Steam writes to it.
            }
        }

        return found;
    }

    // FileSystemWatcher raises its events on the thread pool.
    private void OnManifestChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name is { } name && SteamInstallProgress.AppIdFromFileName(name) is { } appId)
        {
            // Counted at once: a moment later Steam has written more, which would be counted twice.
            var written = SteamWrites();
            dispatcher.BeginInvoke(() => Follow(appId, e.FullPath, justWritten: true, written));
        }
    }

    private void Follow(string appId, string path, bool justWritten, (int, long)? written = null)
    {
        if (_following.TryGetValue(appId, out var followed))
        {
            // The manifest has been written again, with counts that are true now.
            followed.Path = path;
            followed.Baseline = justWritten ? written : followed.Baseline;
            return;
        }

        // Steam rewrites an installed game's manifest too, when it is played or checked. Only an install
        // that has not finished is followed.
        if (ReadManifest(path) is not { } text || SteamInstallProgress.Read(text) is not { Phase: not InstallPhase.Installed })
        {
            return;
        }

        _following[appId] = new Followed(path) { Baseline = justWritten ? written : null };
        if (_timer is null)
        {
            _timer = new DispatcherTimer(Every, DispatcherPriority.Background, (_, _) => Check(), dispatcher);
        }
        _timer.Start();
        Check();
    }

    private void Check()
    {
        var steamNow = SteamWrites();
        var rows = new List<DownloadRow>();
        foreach (var (appId, followed) in _following.ToList())
        {
            var path = followed.Path;
            var tile = state.AllTiles.FirstOrDefault(t => !t.IsInstalled && t.Game.Key == $"steam:{appId}");

            // Cancelled in Steam, or the game uninstalled: Steam deletes the manifest.
            if (!File.Exists(path))
            {
                _following.Remove(appId);
                tile?.ShowInstall(null, 0);
                continue;
            }

            // Being written by Steam this moment; the next look will have it, and the last row stands meanwhile.
            if (ReadManifest(path) is not { } text || SteamInstallProgress.Read(text) is not { } progress)
            {
                if (followed.LastRow is { } last)
                {
                    rows.Add(last);
                }

                continue;
            }

            if (progress.Phase == InstallPhase.Installed)
            {
                _following.Remove(appId);
                tile?.ShowInstall(null, 0);
                if (tile is not null)
                {
                    notices.Show($"{tile.Title} is installed.");
                }

                Program.Log($"Steam finished installing {appId}.");
                _ = rescan();
                continue;
            }

            // Written since the manifest was, by the same Steam process: a restarted Steam counts from nothing.
            long? since = followed.Baseline is { } baseline && steamNow is { } current && baseline.ProcessId == current.ProcessId
                ? current.Written - baseline.Written
                : null;
            var (staged, toStage) = SteamInstallProgress.StageCounts(text);
            var live = SteamInstallProgress.Live(progress, SteamUpdating(appId), staged, toStage, since);
            tile?.ShowInstall(live.Text, live.Fraction);

            followed.LastRow = new DownloadRow(appId, tile?.Title ?? GameName(text) ?? $"Steam app {appId}", live);
            rows.Add(followed.LastRow);
        }

        // The one being downloaded first, then the queue.
        Changed?.Invoke(rows.OrderBy(r => r.Progress.Phase == InstallPhase.Waiting).ToList());

        if (_following.Count == 0)
        {
            _timer?.Stop();
        }
    }

    /// <summary>Told after each look at the installs followed, with each one's progress. Empty once none are left.</summary>
    public event Action<IReadOnlyList<DownloadRow>>? Changed;

    private static string? GameName(string acf) =>
        System.Text.RegularExpressions.Regex.Match(acf, "\"name\"\\s+\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The Steam process and how many bytes it has written to disk, or null when Steam is not running.</summary>
    private static (int ProcessId, long Written)? SteamWrites()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("steam");
        try
        {
            foreach (var process in processes)
            {
                if (Native.BytesWritten(process.Id) is { } written)
                {
                    return (process.Id, written);
                }
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Steam's own flag that it is downloading or updating a game now, which it keeps in the registry.</summary>
    private static bool SteamUpdating(string appId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Valve\Steam\Apps\{appId}");
            return key?.GetValue("Updating") is int updating && updating != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // Steam may be writing the file as it is read, so it is opened without asking anyone to wait.
    private static string? ReadManifest(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
