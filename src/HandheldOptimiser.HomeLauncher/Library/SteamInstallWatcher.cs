using System.IO;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Follows Steam installs, so a game started from Ready to install shows how far it has got on its tile
/// and moves to the installed games by itself when it is done.
///
/// Steam writes a game's <c>appmanifest_&lt;app&gt;.acf</c> as soon as its install is confirmed and keeps
/// the byte counts in it up to date, so the library only has to watch Steam's library folders for those
/// files. Nothing is asked of Steam and no sign-in is needed. An install started in Steam itself is
/// followed the same way.
/// </summary>
internal sealed class SteamInstallWatcher(LibraryState state, NoticePresenter notices, Func<Task> rescan, Dispatcher dispatcher)
{
    // Steam rewrites a manifest every few seconds while it downloads; reading it more often shows nothing new.
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    // The installs being followed: app id, and where its manifest is.
    private readonly Dictionary<string, string> _following = [];
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

        // Installs already under way as the library opened: started in Steam, or before a restart.
        _ = Task.Run(() => UnderWay(folders)).ContinueWith(found =>
        {
            foreach (var (appId, path) in found.Result)
            {
                Follow(appId, path);
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
            dispatcher.BeginInvoke(() => Follow(appId, e.FullPath));
        }
    }

    private void Follow(string appId, string path)
    {
        if (_following.ContainsKey(appId))
        {
            _following[appId] = path;
            return;
        }

        // Steam rewrites an installed game's manifest too, when it is played or checked. Only an install
        // that has not finished is followed.
        if (ReadManifest(path) is not { } text || SteamInstallProgress.Read(text) is not { Phase: not InstallPhase.Installed })
        {
            return;
        }

        _following[appId] = path;
        if (_timer is null)
        {
            _timer = new DispatcherTimer(Every, DispatcherPriority.Background, (_, _) => Check(), dispatcher);
        }
        _timer.Start();
        Check();
    }

    private void Check()
    {
        foreach (var (appId, path) in _following.ToList())
        {
            var tile = state.AllTiles.FirstOrDefault(t => !t.IsInstalled && t.Game.Key == $"steam:{appId}");

            // Cancelled in Steam, or the game uninstalled: Steam deletes the manifest.
            if (!File.Exists(path))
            {
                _following.Remove(appId);
                tile?.ShowInstall(null, 0);
                continue;
            }

            // Being written by Steam this moment; the next look will have it.
            if (ReadManifest(path) is not { } text || SteamInstallProgress.Read(text) is not { } progress)
            {
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

            tile?.ShowInstall(progress.Text, progress.Fraction);
        }

        if (_following.Count == 0)
        {
            _timer?.Stop();
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
