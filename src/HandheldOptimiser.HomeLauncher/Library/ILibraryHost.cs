using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the parts of the library ask of the window that holds them: to show the tiles again after a change,
/// to put the focus back, to look for games, to start one, and what the layout and the game being played are.
/// The window is the one place that knows the order these have to happen in, so the parts ask it rather than
/// each other.
/// </summary>
internal interface ILibraryHost
{
    /// <summary>The library's window, which dialogs are opened over and Windows is told about.</summary>
    Window Window { get; }

    /// <summary>The layout on screen now, which docked mode can make differ from the setting.</summary>
    LibraryLayout Layout { get; }

    /// <summary>Whether the library is on a TV or monitor.</summary>
    bool Docked { get; }

    /// <summary>The game being played, paused or not, or null.</summary>
    GameSession? Session { get; }

    /// <summary>Shows the open tab's tiles again, and everything around them, after the games or a setting changed.</summary>
    void ShowTiles();

    /// <summary>Opens a tab: its games, and its Old School category.</summary>
    void ShowTab(TileFilter tab);

    /// <summary>Puts the focus back on the game it was on. True is the top bar or the search asking to leave.</summary>
    void RestoreFocus(bool leavingTopBar = false);

    /// <summary>Looks for games again.</summary>
    /// <param name="refreshEmulators">Look for emulators again too, which takes a moment.</param>
    Task ScanAsync(bool refreshEmulators = false);

    /// <summary>Starts a game, opens an app, or opens a store's page for a game that is not installed.</summary>
    void Launch(GameTile tile);

    /// <summary>Draws the layout again if a layout or docked mode setting asks for a different one.</summary>
    void LayoutSettingsChanged();

    /// <summary>Resumes a game Quick Resume paused, for anything that brings it back or ends it.</summary>
    void ResumeGame(GameSession session);

    /// <summary>Asks a game to close, and ends it when it does not.</summary>
    Task CloseGameAsync(GameSession session);

    /// <summary>Whether a game is the one paused now.</summary>
    bool IsPaused(GameTile tile);
}
