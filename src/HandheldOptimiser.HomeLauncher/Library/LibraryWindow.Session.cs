namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The game being played: timed for its play time, and once it closes, its profile undone, the programs
/// closed for it started again, and its store's app closed if the player asked for that.
/// </summary>
public partial class LibraryWindow
{
    // A game the library cannot follow (it has no install folder to look in) counts as closed once the
    // player is back in the library after this long.
    private static readonly TimeSpan UnfollowedGrace = TimeSpan.FromSeconds(30);

    // Stores sync saves to the cloud as a game closes, so their app is given a moment first.
    private static readonly TimeSpan StoreCloseDelay = TimeSpan.FromSeconds(20);

    private GameSession? _session;
    private bool _sessionStoreWasOpen;

    // Programs closed for the game being played, to start again once it closes.
    private List<ClosedApp> _closedApps = [];

    private SortOrder _sort = LibrarySettings.Sort;

    private void StartSession(Game game, bool storeWasOpen, bool closeApps)
    {
        var session = new GameSession(game);
        _session = session;
        _sessionStoreWasOpen = storeWasOpen;

        if (closeApps)
        {
            _ = CloseAppsForAsync(session);
        }

        if (session.CanFollow)
        {
            _ = FollowAsync(session);
        }
    }

    private async Task CloseAppsForAsync(GameSession session)
    {
        var keep = GameSession.ProcessFolder(session.Game) is { } folder ? new[] { folder } : [];

        // Each program is given a few seconds to close, so this stays off the UI thread.
        var closed = await Task.Run(() => BackgroundApps.CloseChosen(keep));

        // A program closed twice, for this game and the one before, is started again once.
        _closedApps.AddRange(closed.Where(c => !_closedApps.Any(a => a.Name == c.Name)));

        // A game that ended while they were closing has already had its programs started again.
        if (!ReferenceEquals(_session, session))
        {
            ReopenClosedApps();
        }
    }

    private async Task FollowAsync(GameSession session)
    {
        await session.FollowAsync();

        // Replaced by another game, whose start already counted this one's time.
        if (ReferenceEquals(_session, session))
        {
            OnSessionEnded(session);
        }
    }

    /// <summary>Stops following the current game, counting its time so far, for a new game starting.</summary>
    private void StopFollowing()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.Stop();
        RecordPlaytime(session);
    }

    /// <summary>Puts everything back, for the library closing in the middle of a game.</summary>
    private void EndSession()
    {
        StopFollowing();
        RestoreProfileSettings();

        // Started before returning: the library's process ends right after this.
        ReopenClosedApps(wait: true);
    }

    private void OnSessionEnded(GameSession session)
    {
        _session = null;
        RecordPlaytime(session);
        RestoreProfileSettings();
        ReopenClosedApps();
        _ = CloseStoreAppLaterAsync(session.Game.Store, _sessionStoreWasOpen);

        if (session.Played >= TimeSpan.FromMinutes(1))
        {
            StatusText.Text = $"Played {session.Game.Title} for {PlayStats.Duration(session.Played)}.";
        }

        _ = RefreshStatsAsync();
    }

    /// <summary>Reads the play times again after a game, which may move it in the grid.</summary>
    private async Task RefreshStatsAsync()
    {
        // Steam rewrites its file, several hundred KB on a big library, as a game closes.
        var stats = await Task.Run(() => Playtime.Read(GameCatalog.LastLaunched()));
        if (SameStats(stats, _stats))
        {
            return;
        }

        _stats = stats;

        // Rebuilding the grid under an open menu would lose its place, so that waits for the next scan.
        if (_menu is null)
        {
            ApplyFilter();
            RestoreFocus();
        }
    }

    /// <summary>For a game with no folder to follow it by: over once the player has been back a little while.</summary>
    private void CheckUnfollowedSession()
    {
        if (_session is { CanFollow: false } session && DateTime.UtcNow - session.LaunchedAt > UnfollowedGrace)
        {
            OnSessionEnded(session);
        }
    }

    private static void RecordPlaytime(GameSession session)
    {
        var played = session.Played;
        if (played > TimeSpan.Zero)
        {
            Playtime.Add(session.Game, played);
            Program.Log($"{session.Game.Key} played for {played:hh\\:mm\\:ss}");
        }
    }

    private void ReopenClosedApps(bool wait = false)
    {
        if (_closedApps.Count == 0)
        {
            return;
        }

        var closed = _closedApps;
        _closedApps = [];
        if (!LibrarySettings.ReopenApps)
        {
            return;
        }

        if (wait)
        {
            BackgroundApps.Reopen(closed);
        }
        else
        {
            _ = Task.Run(() => BackgroundApps.Reopen(closed));
        }
    }

    private async Task CloseStoreAppLaterAsync(GameStore store, bool wasOpen)
    {
        var closing = LibrarySettings.StoreClosing;
        if (closing == StoreClosing.Off || (closing == StoreClosing.OpenedByGame && wasOpen) || StoreClients.For(store) is null)
        {
            return;
        }

        await Task.Delay(StoreCloseDelay);

        // Another of its games started meanwhile, which still needs it.
        if (_session?.Game.Store == store)
        {
            return;
        }

        await Task.Run(() => StoreClients.Close(store));
    }

    /// <summary>The tiles in the player's chosen order. Ties keep the scan's order: last launched, then by name.</summary>
    private IEnumerable<GameTile> Sorted(IEnumerable<GameTile> tiles) => _sort switch
    {
        SortOrder.MostPlayed => tiles.OrderByDescending(t => _stats.GetValueOrDefault(t.Game.Key).Played),
        SortOrder.Name => tiles.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => tiles.OrderByDescending(t => _stats.GetValueOrDefault(t.Game.Key).LastPlayed ?? DateTimeOffset.MinValue)
    };

    private static bool SameStats(Dictionary<string, PlayStats> a, Dictionary<string, PlayStats> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);
}
