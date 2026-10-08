using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Quick Resume: coming back to the library pauses the game being played, so it stops using the
/// processor, the graphics and the battery, and picking it again carries on exactly where it was. A
/// paused game still holds its memory, so only one is kept: starting another game closes it, on a
/// second press. Games with anti-cheat keep running, since pausing one could get the player thrown off.
///
/// A paused game survives sleep and hibernate, not a restart. Whatever brings it back to the front
/// (picking it here, Alt+Tab, waking with Back to your game after sleep) resumes it first.
/// </summary>
public partial class LibraryWindow
{
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
            if (!IsActive || !ReferenceEquals(_session, session) || !LibrarySettings.QuickResume)
            {
                return;
            }

            var returns = _returns;
            var outcome = await Task.Run(session.Pause);

            // Back in the game, or on to another, while it was being paused.
            if (outcome == PauseOutcome.Paused && (!IsActive || returns != _returns || !ReferenceEquals(_session, session)))
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
                StatusText.Text = $"{title} is paused. Press A on it to carry on.";
                RefreshPausedGame();
                break;

            case PauseOutcome.AntiCheat when !ReferenceEquals(_toldNotPaused, session):
                _toldNotPaused = session;
                StatusText.Text = $"{title} keeps running behind the library: it uses {session.AntiCheat}, which pausing could trip.";
                break;

            case PauseOutcome.Refused when !ReferenceEquals(_toldNotPaused, session):
                _toldNotPaused = session;
                StatusText.Text = $"{title} keeps running behind the library: Windows would not let it be paused.";
                break;
        }
    }

    /// <summary>Resumes the game if it is paused, for anything that brings it back or ends it.</summary>
    private void ResumeGame(GameSession session)
    {
        StopWatchingForeground();
        if (session.Resume())
        {
            RefreshPausedGame();
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
        if (window == 0 || !ReferenceEquals(_session, session) || !IsActive)
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
        if (IsActive && ReferenceEquals(_session, session))
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
            StatusText.Text = $"{paused.Game.Title} is paused. Press A again to close it and start {tile.Title}. Anything not saved in it is lost.";
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

    private async Task CloseGameAsync(GameSession session)
    {
        StatusText.Text = $"Closing {session.Game.Title}...";
        StopWatchingForeground();
        await Task.Run(() => session.Close(CloseWait));
        StatusText.Text = $"{session.Game.Title} closed.";
        RefreshPausedGame();
    }

    /// <summary>The game's paused mark, on the Continue playing card and the row layout's details, after it changes.</summary>
    private void RefreshPausedGame()
    {
        UpdateContinue();
        if (Keyboard.FocusedElement is Button { DataContext: GameTile tile } && _menu is null)
        {
            ShowRowDetails(tile);
        }
    }

    /// <summary>Whether this is the game paused right now.</summary>
    private bool IsPausedGame(GameTile tile) => _session is { IsPaused: true } session && session.Game.Key == tile.Game.Key;

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

    private void StopWatchingForeground()
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

    // ----- Quick actions: Close game -----

    private void RefreshCloseGameItem(GameTile tile)
    {
        var running = _session is { StartedAt: not null } session && session.Game.Key == tile.Game.Key;
        CloseGameItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CloseGameItem.Tag = ReferenceEquals(_confirming, CloseGameItem) ? "Press again to close it"
            : IsPausedGame(tile) ? "It's paused. Anything not saved in it is lost"
            : "Anything not saved in it is lost";
    }

    private void OnCloseGame(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile || _session is not { StartedAt: not null } session || session.Game.Key != tile.Game.Key)
        {
            return;
        }

        if (!ReferenceEquals(_confirming, CloseGameItem))
        {
            _confirming = CloseGameItem;
            RefreshCloseGameItem(tile);
            return;
        }

        CloseMenu();
        _ = CloseGameAsync(session);
    }

    private void OnCloseGameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(_confirming, CloseGameItem))
        {
            _confirming = null;
            if (_menuTile is { } tile)
            {
                RefreshCloseGameItem(tile);
            }
        }
    }

    // ----- Settings: While playing -----

    private void OnToggleQuickResume(object sender, RoutedEventArgs e)
    {
        LibrarySettings.QuickResume = !LibrarySettings.QuickResume;

        // Switched off with a game paused: it runs again, behind the library, as it would have.
        if (!LibrarySettings.QuickResume && _session is { } session)
        {
            ResumeGame(session);
        }

        RefreshQuickResumeSetting();
    }

    private void RefreshQuickResumeSetting() =>
        QuickResumeSetting.Tag = LibrarySettings.QuickResume
            ? "On: your game pauses while the library is in front, until you pick it again"
            : "Off: your game keeps running behind the library";
}
