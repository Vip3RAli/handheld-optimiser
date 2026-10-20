namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What one press of the Armoury Crate button does to the library.</summary>
internal enum ButtonAction
{
    /// <summary>Bring the library to the front.</summary>
    ShowLibrary,

    /// <summary>Put the game being played back in front, as A on its tile does.</summary>
    BackToGame,

    /// <summary>Get out of the way, so whatever was behind the library is in front again.</summary>
    Minimise,

    /// <summary>Leave the library in front, as there is nothing behind it to go back to.</summary>
    Stay
}

/// <summary>
/// The Armoury Crate button works as Armoury Crate's own did: one press brings the library forward, and
/// a press while it is in front sends the player back to their game, or to whatever was there before.
/// </summary>
internal static class ButtonPress
{
    /// <param name="fullScreenExperience">Whether Windows is in its full screen experience (Xbox mode), where
    /// there is no desktop behind the library, so minimising it would leave a blank screen.</param>
    public static ButtonAction Decide(bool libraryInFront, bool gameRunning, bool fullScreenExperience) =>
        !libraryInFront ? ButtonAction.ShowLibrary
        : gameRunning ? ButtonAction.BackToGame
        : fullScreenExperience ? ButtonAction.Stay
        : ButtonAction.Minimise;

    /// <summary>What to do when the game could not be put back in front.</summary>
    public static ButtonAction WhenGameWillNotReturn(bool fullScreenExperience) =>
        fullScreenExperience ? ButtonAction.Stay : ButtonAction.Minimise;
}
