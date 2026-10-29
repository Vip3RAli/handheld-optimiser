using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Playing a game from the library, from the press that starts it to the moment it closes.
///
/// The game being played is timed for its play time, held at its frame rate by TDP Engine, and switched to
/// its profile. Once it closes, its profile is undone, the programs closed for it are started again, and its
/// store's app is closed if the player asked for that.
///
/// Quick Resume: coming back to the library pauses the game, so it stops using the processor, the graphics
/// and the battery, and picking it again carries on exactly where it was. A paused game still holds its
/// memory, so only one is kept: starting another game closes it, on a second press. Games with anti-cheat
/// keep running, since pausing one could get the player thrown off. A paused game survives sleep and
/// hibernate, not a restart. Whatever brings it back to the front resumes it first.
///
/// Back to your game after sleep: when the device wakes with a game running and the library in front, the
/// game is put back in front. A Modern Standby device such as the Ally does not tell programs it is going to
/// sleep the old way; its screen turns off instead, so the screen going off and on again counts as sleep and
/// wake too.
///
/// And the Armoury Crate button, passed on by a second start of the launcher.
/// </summary>
internal sealed class GameSessionController
{
    // Guards against a second A press starting the game twice while its store client wakes up.
    private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(5);

    private readonly Window _window;
    private readonly LibraryState _state;
    private readonly LibraryNavigation _navigation;
    private readonly NoticePresenter _notices;
    private readonly ContinuePresenter _continue;
    private readonly ILibraryHost _host;
    private readonly Action _screenOff;
    private readonly Action _screenOn;
    private readonly Action _showLibrary;

    private DateTime _launchBlockedUntil = DateTime.MinValue;

    /// <param name="screenOff">Stops what has no reason to run while the screen is off: the controller polling and the clock.</param>
    /// <param name="screenOn">Starts them again, and reads the clock and the battery.</param>
    /// <param name="showLibrary">Brings the library to the front, as the home button does.</param>
    public GameSessionController(Window window, LibraryState state, LibraryNavigation navigation, NoticePresenter notices,
        ContinuePresenter continuePresenter, ILibraryHost host, Action screenOff, Action screenOn, Action showLibrary)
    {
        _window = window;
        _state = state;
        _navigation = navigation;
        _notices = notices;
        _continue = continuePresenter;
        _host = host;
        _screenOff = screenOff;
        _screenOn = screenOn;
        _showLibrary = showLibrary;
    }

    /// <summary>The game being played, paused or not, or null.</summary>
    public GameSession? Session => _session;

    /// <summary>Whether the screen is off, when the controller and the clock are left stopped.</summary>
    public bool DisplayOff => _displayOff;

    /// <summary>
    /// The library has come to the front. Just woken with a game running: straight back to it. Otherwise the
    /// game is paused, with Quick Resume.
    /// </summary>
    public void Returned()
    {
        _ = ResumeGameAsync();
        if (!_resuming && !JustWoke)
        {
            PauseOnReturn();
        }
    }

    /// <summary>
    /// The rest of coming back to the front: a game the library cannot follow is over once the player has
    /// been back a while, and what TDP Engine is doing for a game it holds is said for a moment.
    /// </summary>
    public void CheckWhileAway()
    {
        CheckUnfollowedSession();
        _ = ShowAutoTdpAsync();
    }

    /// <summary>The player asked for the library, so waking up does not send them back to their game.</summary>
    public void ForgetWake() => _wokeAt = null;

    // ===== Launching =====

    /// <summary>Starts a game, opens an app, or opens a store's page for a game that is not installed.</summary>
    public void Launch(GameTile tile)
    {
        if (DateTime.UtcNow < _launchBlockedUntil)
        {
            return;
        }

        _state.FocusedKey = tile.Game.Key;

        // An app opens beside the game, which stays as it is, paused or not. Apps never have a session,
        // so they are never paused themselves.
        if (tile.IsInstalled && !tile.IsApp && _session is { } session)
        {
            // The game being played, paused or not: back into it.
            if (session.StartedAt is not null && session.Game.Key == tile.Game.Key)
            {
                _launchBlockedUntil = DateTime.UtcNow + LaunchCooldown;
                _ = BackToGameAsync(session, tile);
                return;
            }

            // Only one game is kept paused.
            if (session.IsPaused)
            {
                ConfirmClosePaused(session, tile);
                return;
            }
        }

        StartGame(tile);
    }

    private void StartGame(GameTile tile)
    {
        _launchBlockedUntil = DateTime.UtcNow + LaunchCooldown;
        _state.FocusedKey = tile.Game.Key;
        _closeArmed = null;

        // A game that is not installed opens its store's install page, with nothing else to set up.
        if (!tile.IsInstalled)
        {
            var problem = GameCatalog.Launch(tile.Game);
            _notices.Show(problem ?? $"Opening {Game.NameOf(tile.Game.Store)} to install {tile.Title}...", tile);
            if (problem is not null)
            {
                _launchBlockedUntil = DateTime.MinValue;
            }

            return;
        }

        // An app just opens. Whatever is set up for a game that is running stays as it is.
        if (tile.IsApp)
        {
            var failed = GameCatalog.Launch(tile.Game);
            _notices.Show(failed ?? $"Opening {tile.Title}...", tile);
            if (failed is not null)
            {
                _launchBlockedUntil = DateTime.MinValue;
            }

            return;
        }

        // The last game's profile never carries over to the next game.
        RestoreProfileSettings();
        var profile = GameProfiles.For(tile.Game);
        var storeWasOpen = StoreClients.IsRunning(tile.Game.Store);

        var error = GameCatalog.Launch(tile.Game);
        _notices.Show(error ?? $"Launching {tile.Title}...", tile, error is null ? NoticePresenter.LaunchShownFor : null);
        if (error is not null)
        {
            _launchBlockedUntil = DateTime.MinValue;
            return;
        }

        // The last game's play time is counted up to now. Programs closed for it stay closed for this one.
        StopFollowing();

        if (!profile.IsEmpty)
        {
            ApplyProfile(tile.Game, profile);
        }

        StartSession(tile.Game, storeWasOpen, profile.CloseApps ?? LibrarySettings.GameMode,
            profile.AutoTdpFps ?? LibrarySettings.AutoTdpFps, profile.FanCurve ?? LibrarySettings.FanCurve);
    }


    // ===== The game being played =====

    // A game the library cannot follow (it has no install folder to look in) counts as closed once the
    // player is back in the library after this long.
    private static readonly TimeSpan UnfollowedGrace = TimeSpan.FromSeconds(30);

    // Stores sync saves to the cloud as a game closes, so their app is given a moment first.
    private static readonly TimeSpan StoreCloseDelay = TimeSpan.FromSeconds(20);

    private GameSession? _session;
    private bool _sessionStoreWasOpen;

    // The game TDP Engine is holding, or setting the fans for, or null.
    private Game? _autoTdpGame;

    // Programs closed for the game being played, to start again once it closes.
    private List<ClosedApp> _closedApps = [];

    /// <param name="autoTdpFps">The frame rate TDP Engine holds the game at, or 0 for off.</param>
    /// <param name="fanCurve">The fan curve to play with, one of <see cref="AutoTdp.FanChoices"/>.</param>
    private void StartSession(Game game, bool storeWasOpen, bool closeApps, int autoTdpFps, string fanCurve)
    {
        var session = new GameSession(game);
        _session = session;
        _sessionStoreWasOpen = storeWasOpen;

        var fullScreen = Native.IsFullScreenExperience();
        var shouldOptimiseWindows = LibrarySettings.WindowsGameMode switch
        {
            WindowsGameMode.Always => true,
            WindowsGameMode.XboxMode => fullScreen,
            _ => false
        };

        if (shouldOptimiseWindows)
        {
            _ = Task.Run(WindowsProcessOptimiser.Optimise);
        }

        if (closeApps)
        {
            _ = CloseAppsForAsync(session);
        }

        // Without a folder there is no telling when the game closes, and so when to stop.
        if ((autoTdpFps > 0 || fanCurve != AutoTdp.AllyFans) && session.CanFollow)
        {
            _autoTdpGame = game;
            _ = StartAutoTdpAsync(game, autoTdpFps, fanCurve);
        }

        if (session.CanFollow)
        {
            _ = FollowAsync(session);
        }
    }

    private async Task StartAutoTdpAsync(Game game, int fps, string fanCurve)
    {
        if (await AutoTdp.StartAsync(game, fps, fanCurve) is { } problem)
        {
            _notices.Show(problem);
            if (ReferenceEquals(_autoTdpGame, game))
            {
                _autoTdpGame = null;
            }
        }
    }

    /// <summary>Hands the power limit and fans back to Armoury Crate's operating mode, for the game closing.</summary>
    private void StopAutoTdp()
    {
        if (_autoTdpGame is { } game)
        {
            _autoTdpGame = null;
            _ = AutoTdp.StopAsync(game);
        }
    }

    /// <summary>What TDP Engine is doing, as the library comes back in front of a game it is holding.</summary>
    private async Task ShowAutoTdpAsync()
    {
        if (_autoTdpGame is not null && await AutoTdp.DescribeAsync() is { } line && _autoTdpGame is not null)
        {
            _notices.Show(line);
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
        ResumeGame(session);
        session.Stop();
        RecordPlaytime(session);
        StopAutoTdp();
    }

    // Set once EndSession has run, so the window closing and LibraryApp's finally do not both do it.
    private bool _sessionEnded;

    /// <summary>
    /// Puts everything back, for the library closing in the middle of a game. Runs once: from the window
    /// closing, and again from LibraryApp.Run's finally in case the library failed before it could close.
    /// </summary>
    public void EndSession()
    {
        if (_sessionEnded)
        {
            return;
        }

        _sessionEnded = true;
        StopFollowing();
        RestoreProfileSettings();

        // Started before returning: the library's process ends right after this.
        ReopenClosedApps(wait: true);
    }

    private void OnSessionEnded(GameSession session)
    {
        _session = null;

        // Ended while paused, from Task Manager say: lets go of its processes.
        ResumeGame(session);
        RecordPlaytime(session);
        StopAutoTdp();
        RestoreProfileSettings();
        ReopenClosedApps();
        _ = CloseStoreAppLaterAsync(session.Game.Store, _sessionStoreWasOpen);

        if (session.Played >= TimeSpan.FromMinutes(1))
        {
            _notices.Show($"Played {session.Game.Title} for {PlayStats.Duration(session.Played)}.");
        }

        _ = RefreshStatsAsync();
    }

    /// <summary>Reads the play times again after a game, which may move it in the grid.</summary>
    private async Task RefreshStatsAsync()
    {
        // Steam rewrites its file, several hundred KB on a big library, as a game closes.
        var stats = await Task.Run(() => Playtime.Read(GameCatalog.LastLaunched()));
        if (LibraryState.SameStats(stats, _state.Stats))
        {
            return;
        }

        _state.Stats = stats;

        // Rebuilding the grid under an open menu would lose its place, so that waits for the next scan.
        if (!_navigation.MenuOpen)
        {
            _host.ShowTiles();
            _host.RestoreFocus();
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


    // ===== Per-game profiles =====

    /// <summary>The settings from before a profiled game started. Only what its profile changed is set.</summary>
    private sealed record SavedSettings(Game Game, DateTime StartedAt, PowerMode? PowerMode, int? RefreshRate, int? Brightness,
        Resolution? Resolution = null);

    private SavedSettings? _saved;

    /// <summary>Switches to a game's profile as it starts, keeping what it replaces.</summary>
    private void ApplyProfile(Game game, GameProfile profile)
    {
        var powerMode = profile.PowerMode is not null ? PowerModes.Read() : null;
        var resolution = profile.Resolution is not null ? Resolutions.Read()?.Current : null;

        // A new size can come with a different refresh rate, so the rate is kept to put back as well.
        var refreshRate = profile.RefreshRate is not null || resolution is not null ? RefreshRates.Read()?.Current : null;
        _saved = new SavedSettings(game, DateTime.UtcNow, powerMode, refreshRate, null, resolution);

        if (profile.PowerMode is { } mode && powerMode is not null && powerMode != mode && !PowerModes.Set(mode))
        {
            Program.Log($"Could not switch to the power mode in {game.Key}'s profile");
        }

        // The size first: the refresh rate is then set for the new size.
        if (profile.Resolution is { } size && resolution is not null && resolution != size
            && Resolutions.Set(size) is { } sizeError)
        {
            Program.Log($"Could not switch to the resolution in {game.Key}'s profile: {sizeError}");
        }

        if (profile.RefreshRate is { } hertz && refreshRate is not null && refreshRate != hertz && RefreshRates.Set(hertz) is { } error)
        {
            Program.Log($"Could not switch to the refresh rate in {game.Key}'s profile: {error}");
        }

        if (profile.Brightness is { } level)
        {
            _ = ApplyProfileBrightnessAsync(game, level);
        }

        Program.Log($"Switched to {game.Key}'s profile");
    }

    private async Task ApplyProfileBrightnessAsync(Game game, int level)
    {
        // WMI is slow, so the reading and the change both happen off the UI thread.
        var before = await Task.Run(() => Brightness.Read() is { } now && Brightness.Set(level) ? now : (int?)null);
        if (before is not null && _saved is { } saved && saved.Game == game)
        {
            _saved = saved with { Brightness = before };
        }
    }

    /// <summary>Puts back the settings a profiled game replaced, if any.</summary>
    private void RestoreProfileSettings()
    {
        if (_saved is not { } saved)
        {
            return;
        }

        _saved = null;

        if (saved.PowerMode is { } mode)
        {
            PowerModes.Set(mode);
        }

        if (saved.Resolution is { } size && Resolutions.Set(size, saved.RefreshRate) is { } sizeError)
        {
            Program.Log($"Could not put the resolution back: {sizeError}");
        }

        if (saved.RefreshRate is { } hertz && RefreshRates.Set(hertz) is { } error)
        {
            Program.Log($"Could not put the refresh rate back: {error}");
        }

        if (saved.Brightness is { } level)
        {
            _ = Task.Run(() => Brightness.Set(level));
        }

        Program.Log($"Put back the settings {saved.Game.Key}'s profile replaced");
    }

    // ===== Quick Resume =====

    // Games mute or pause themselves as they lose the focus; that is let happen before they are frozen.
    private static readonly TimeSpan PauseDelay = TimeSpan.FromSeconds(1.5);

    // How long a game asked to close may take, for saving, before it is ended.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(8);

    // How long the second press that closes a paused game, to start another, stays armed.
    private static readonly TimeSpan CloseArmedFor = TimeSpan.FromSeconds(10);

    private nint _foregroundHook;
    private bool _pausing;

    // Counts the returns to the game, so a pause that finishes after the player went back is undone.
    private int _returns;

    // The game already told why it keeps running, so that is said once a game, not on every return.
    private GameSession? _toldNotPaused;

    // Another game's tile, pressed once while a game is paused: pressed again in time, the paused game
    // is closed and this one starts.
    private (string Key, DateTime Until)? _closeArmed;

    /// <summary>Pauses the game being played, now the library is in front. Runs on every activation.</summary>
    private async void PauseOnReturn()
    {
        if (!LibrarySettings.QuickResume || _pausing || _session is not { StartedAt: not null, IsPaused: false } session)
        {
            return;
        }

        _pausing = true;
        try
        {
            await Task.Delay(PauseDelay);
            if (!_window.IsActive || !ReferenceEquals(_session, session) || !LibrarySettings.QuickResume)
            {
                return;
            }

            var returns = _returns;
            var outcome = await Task.Run(session.Pause);

            // Back in the game, or on to another, while it was being paused.
            if (outcome == PauseOutcome.Paused && (!_window.IsActive || returns != _returns || !ReferenceEquals(_session, session)))
            {
                session.Resume();
                return;
            }

            ReportPause(session, outcome);
        }
        finally
        {
            _pausing = false;
        }
    }

    private void ReportPause(GameSession session, PauseOutcome outcome)
    {
        var title = session.Game.Title;
        switch (outcome)
        {
            case PauseOutcome.Paused:
                WatchForeground();
                _notices.Show($"{title} is paused. Press A on it to carry on.");
                _continue.Update();
                break;

            case PauseOutcome.AntiCheat when !ReferenceEquals(_toldNotPaused, session):
                _toldNotPaused = session;
                _notices.Show($"{title} keeps running behind the library: it uses {session.AntiCheat}, and pausing it could get you flagged.");
                break;

            case PauseOutcome.Refused when !ReferenceEquals(_toldNotPaused, session):
                _toldNotPaused = session;
                _notices.Show($"{title} keeps running behind the library: Windows would not let it be paused.");
                break;
        }
    }

    /// <summary>Resumes the game if it is paused, for anything that brings it back or ends it.</summary>
    public void ResumeGame(GameSession session)
    {
        StopWatchingForeground();
        if (session.Resume())
        {
            _continue.Update();
        }
    }

    /// <summary>
    /// Back into the game being played: resumed, and its window put in front. Windows only allows that
    /// while the library is in front.
    /// </summary>
    /// <returns>False when the game has no window to go back to.</returns>
    private async Task<bool> ReturnToGameAsync(GameSession session)
    {
        _returns++;
        ResumeGame(session);

        // Looking through the running programs takes a moment, so it stays off the UI thread.
        var window = await Task.Run(session.MainWindow);
        if (window == 0 || !ReferenceEquals(_session, session) || !_window.IsActive)
        {
            return false;
        }

        return Native.SwitchTo(window);
    }

    /// <summary>A on the game being played: straight back into it, rather than starting it through its store again.</summary>
    private async Task BackToGameAsync(GameSession session, GameTile tile)
    {
        if (await ReturnToGameAsync(session))
        {
            Program.Log($"Back to {session.Game.Key}");
            return;
        }

        // Closing as A was pressed, or its window is hidden: started the usual way, which a running game shrugs off.
        _launchBlockedUntil = DateTime.MinValue;
        if (_window.IsActive && ReferenceEquals(_session, session))
        {
            StartGame(tile);
        }
    }

    /// <summary>
    /// A on another game while one is paused. The first press says the paused one will close; the second
    /// closes it and starts the new one.
    /// </summary>
    private void ConfirmClosePaused(GameSession paused, GameTile tile)
    {
        if (_closeArmed is not { } armed || armed.Key != tile.Game.Key || DateTime.UtcNow > armed.Until)
        {
            _closeArmed = (tile.Game.Key, DateTime.UtcNow + CloseArmedFor);
            // On the card for as long as the second press counts.
            _notices.Show($"{paused.Game.Title} is paused. Press A again to close it and start {tile.Title}. Anything not saved in it is lost.",
                tile, CloseArmedFor);
            return;
        }

        _closeArmed = null;
        _ = CloseThenStartAsync(paused, tile);
    }

    private async Task CloseThenStartAsync(GameSession paused, GameTile tile)
    {
        _launchBlockedUntil = DateTime.MaxValue;
        try
        {
            await CloseGameAsync(paused);

            // Its time is counted now. What was changed for it is put back, or carried over, as the new game starts.
            if (ReferenceEquals(_session, paused))
            {
                StopFollowing();
            }
        }
        finally
        {
            _launchBlockedUntil = DateTime.MinValue;
        }

        StartGame(tile);
    }

    /// <summary>Asks a game to close, and ends it when it does not.</summary>
    public async Task CloseGameAsync(GameSession session)
    {
        _notices.Show($"Closing {session.Game.Title}...");
        StopWatchingForeground();
        await Task.Run(() => session.Close(CloseWait));
        _notices.Show($"{session.Game.Title} closed.");
        _continue.Update();
    }

    /// <summary>Whether this is the game paused right now.</summary>
    public bool IsPaused(GameTile tile) => _session is { IsPaused: true } session && session.Game.Key == tile.Game.Key;

    // ----- Going back to a paused game some other way -----

    private void WatchForeground()
    {
        if (_foregroundHook != 0)
        {
            return;
        }

        Native.ForegroundChanged += OnForegroundChanged;
        _foregroundHook = Native.WatchForeground();
        if (_foregroundHook == 0)
        {
            Native.ForegroundChanged -= OnForegroundChanged;
            Program.Log("Could not watch for the paused game coming to the front");
        }
    }

    public void StopWatchingForeground()
    {
        if (_foregroundHook == 0)
        {
            return;
        }

        Native.StopWatchingForeground(_foregroundHook);
        Native.ForegroundChanged -= OnForegroundChanged;
        _foregroundHook = 0;
    }

    /// <summary>
    /// The paused game brought to the front some other way, as with Alt+Tab or the taskbar: resumed at
    /// once, before Windows decides it has stopped responding.
    /// </summary>
    private void OnForegroundChanged(nint window)
    {
        if (_session is not { IsPaused: true } session)
        {
            StopWatchingForeground();
            return;
        }

        if (session.IsPausedProcess(Native.WindowProcess(window)) || Native.IsGhostWindow(window))
        {
            _returns++;
            ResumeGame(session);
        }
    }

    // ===== Back to your game after sleep =====

    // Unlocking can take a while, so the game is put back if the library comes to the front within this.
    private static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(2);

    // Windows puts the lock screen up, or the last program back in front, just after waking.
    private static readonly TimeSpan SettleAfterWake = TimeSpan.FromSeconds(2);

    private HwndSource? _hwndSource;
    private nint _displayNotification;
    private bool _displayOff;

    // When the device last woke, while it still counts as just woken. Null once the game is back in front,
    // or when the player pressed the home button to come to the library.
    private DateTime? _wokeAt;
    private bool _resuming;

    /// <summary>Whether the device woke a moment ago and the game is still to be put back in front.</summary>
    private bool JustWoke => _wokeAt is { } woke && DateTime.UtcNow - woke <= ResumeWithin;

    /// <summary>Starts listening for the device waking and the screen going off and on, once the window has a handle.</summary>
    public void WatchWake()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(OnWindowMessage);
        _displayNotification = Native.RegisterPowerSettingNotification(handle, Native.ConsoleDisplayState, 0);
    }

    public void StopWatchingWake()
    {
        _hwndSource?.RemoveHook(OnWindowMessage);
        if (_displayNotification != 0)
        {
            Native.UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = 0;
        }
    }

    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.WmPowerBroadcast)
        {
            return 0;
        }

        switch ((int)wParam)
        {
            case Native.PbtApmResumeAutomatic or Native.PbtApmResumeSuspend:
                OnWake();
                break;

            case Native.PbtPowerSettingChange when lParam != 0:
                var setting = Marshal.PtrToStructure<Native.PowerBroadcastSetting>(lParam);
                if (setting.PowerSetting == Native.ConsoleDisplayState && setting.DataLength >= sizeof(uint))
                {
                    // 0 is off, 1 is on and 2 is dimmed, which is still on.
                    if (setting.Data == 0)
                    {
                        _displayOff = true;
                        OnDisplayOff();
                    }
                    else if (setting.Data == 1 && _displayOff)
                    {
                        _displayOff = false;
                        OnDisplayOn();
                        OnWake();
                    }
                }

                break;
        }

        return 0;
    }

    /// <summary>
    /// Nobody can see the library with the screen off, and on a Modern Standby device it stays running
    /// then: the controller is polled every 16 ms while it is in front, which kept the processor from
    /// resting the whole time the device was asleep. Polling and the clock stop until the screen is back.
    /// </summary>
    private void OnDisplayOff()
    {
        _screenOff();
    }

    /// <summary>Starts again what <see cref="OnDisplayOff"/> stopped, if the library is still in front.</summary>
    private void OnDisplayOn()
    {
        if (!_window.IsActive)
        {
            return;
        }

        _screenOn();
    }

    private async void OnWake()
    {
        if (!LibrarySettings.ResumeAfterSleep || _session is null)
        {
            return;
        }

        _wokeAt = DateTime.UtcNow;
        await Task.Delay(SettleAfterWake);
        await ResumeGameAsync();
    }

    /// <summary>
    /// Puts the game being played back in front, if the device has just woken and the library is in front.
    /// When the library is not in front, the game already is, or the lock screen is: this runs again
    /// when the library is next activated.
    /// </summary>
    private async Task ResumeGameAsync()
    {
        if (_wokeAt is not { } woke || DateTime.UtcNow - woke > ResumeWithin || _resuming)
        {
            return;
        }

        if (!_window.IsActive || _session is not { StartedAt: not null } session)
        {
            return;
        }

        // One go only: if the player comes back to the library after this, that is where they want to be.
        _wokeAt = null;
        _resuming = true;
        try
        {
            // Resumed first, if Quick Resume paused it.
            if (await ReturnToGameAsync(session))
            {
                Program.Log($"Back to {session.Game.Key} after waking up");
            }
        }
        finally
        {
            _resuming = false;
        }
    }

    // ===== The Armoury Crate button =====

    /// <summary>
    /// The Armoury Crate button, passed on by a second start of the launcher. Pressed while the library is
    /// in front it sends the player back, as Armoury Crate did; otherwise it brings the library forward.
    /// </summary>
    public void OnArmouryCrateButton()
    {
        var game = _session is { StartedAt: not null } session ? session : null;
        var fullScreen = Native.IsFullScreenExperience();
        var action = ButtonPress.Decide(_window.IsActive, game is not null, fullScreen);

        // Logged so that a press that goes wrong can be told apart from one that went right.
        Program.Log($"Armoury Crate button: {action} (library in front: {_window.IsActive}, game: {game?.Game.Key ?? "none"}, Xbox mode: {fullScreen})");
        switch (action)
        {
            case ButtonAction.ShowLibrary:
                _ = ShowOverGameAsync(game);
                break;

            case ButtonAction.BackToGame:
                _ = BackToGameFromButtonAsync(game!);
                break;

            case ButtonAction.Minimise:
                _window.WindowState = WindowState.Minimized;
                break;

            case ButtonAction.Stay:
                break;
        }
    }

    /// <summary>
    /// Brings the library forward, over the game if one is running. A full screen game such as Fortnite
    /// keeps its picture on top even after the library takes the focus (seen on the Ally on 2026-10-18),
    /// so its window is minimised first, as Alt+Tab does. Going back into the game restores it.
    /// </summary>
    private async Task ShowOverGameAsync(GameSession? game)
    {
        if (game is not null)
        {
            // Looking through the running programs takes a moment, so it stays off the UI thread.
            var window = await Task.Run(game.MainWindow);
            if (window != 0)
            {
                Native.Minimise(window);
            }
            else
            {
                Program.Log($"Armoury Crate button: no window found for {game.Game.Key}, so it was not minimised");
            }
        }

        _showLibrary();
        BringToFront();
    }

    /// <summary>Back into the game, or out of the way when it has no window to go back to.</summary>
    private async Task BackToGameFromButtonAsync(GameSession session)
    {
        if (await ReturnToGameAsync(session))
        {
            Program.Log($"Back to {session.Game.Key} from the Armoury Crate button");
            return;
        }

        // In Xbox mode there is no desktop behind the library, so it stays rather than leave a blank screen.
        if (ButtonPress.WhenGameWillNotReturn(Native.IsFullScreenExperience()) == ButtonAction.Stay)
        {
            Program.Log($"Armoury Crate button: could not go back to {session.Game.Key} in Xbox mode, so the library stayed");
            return;
        }

        Program.Log($"Armoury Crate button: could not go back to {session.Game.Key} (library in front: {_window.IsActive}), so the library was minimised");
        _window.WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Takes the foreground for this window. Started by TDP Engine's service, the library does not own it,
    /// and Windows only lets it be taken right after input, so this runs as the window is first drawn.
    /// </summary>
    public void BringToFront()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != 0)
        {
            Native.BringToFront(handle);
        }
    }
}
