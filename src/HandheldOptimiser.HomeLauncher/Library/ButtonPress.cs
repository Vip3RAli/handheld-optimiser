namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What one press of the Armoury Crate button does to the library.</summary>
internal enum ButtonAction
{
    /// <summary>Bring the library to the front.</summary>
    ShowLibrary,

    /// <summary>Put the game being played back in front, as A on its tile does.</summary>
    BackToGame,

    /// <summary>Get out of the way, so whatever was behind the library is in front again.</summary>
    Minimise
}

/// <summary>
/// The Armoury Crate button works as Armoury Crate's own did: one press brings the library forward, and
/// a press while it is in front sends the player back to their game, or to whatever was there before.
/// </summary>
internal static class ButtonPress
{
    public static ButtonAction Decide(bool libraryInFront, bool gameRunning) =>
        !libraryInFront ? ButtonAction.ShowLibrary
        : gameRunning ? ButtonAction.BackToGame
        : ButtonAction.Minimise;
}
