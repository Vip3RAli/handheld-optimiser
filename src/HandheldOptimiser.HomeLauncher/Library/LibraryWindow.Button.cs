using System.Windows;
using System.Windows.Interop;

namespace HandheldOptimiser.HomeLauncher.Library;

public partial class LibraryWindow
{
    /// <summary>
    /// The Armoury Crate button, passed on by a second start of the launcher. Pressed while the library is
    /// in front it sends the player back, as Armoury Crate did; otherwise it brings the library forward.
    /// </summary>
    public void OnArmouryCrateButton()
    {
        var game = _session is { StartedAt: not null } session ? session : null;
        var action = ButtonPress.Decide(IsActive, game is not null);

        // Logged so that a press that goes wrong can be told apart from one that went right.
        Program.Log($"Armoury Crate button: {action} (library in front: {IsActive}, game: {game?.Game.Key ?? "none"})");
        switch (action)
        {
            case ButtonAction.ShowLibrary:
                _ = ShowOverGameAsync(game);
                break;

            case ButtonAction.BackToGame:
                _ = BackToGameFromButtonAsync(game!);
                break;

            case ButtonAction.Minimise:
                WindowState = WindowState.Minimized;
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

        ShowLibrary();
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

        Program.Log($"Armoury Crate button: could not go back to {session.Game.Key} (library in front: {IsActive}), so the library was minimised");
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Takes the foreground for this window. Started by TDP Engine's service, the library does not own it,
    /// and Windows only lets it be taken right after input, so this runs as the window is first drawn.
    /// </summary>
    public void BringToFront()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            Native.BringToFront(handle);
        }
    }
}
