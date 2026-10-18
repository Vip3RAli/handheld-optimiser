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
        switch (ButtonPress.Decide(IsActive, game is not null))
        {
            case ButtonAction.ShowLibrary:
                ShowLibrary();
                BringToFront();
                break;

            case ButtonAction.BackToGame:
                _ = BackToGameFromButtonAsync(game!);
                break;

            case ButtonAction.Minimise:
                WindowState = WindowState.Minimized;
                break;
        }
    }

    /// <summary>Back into the game, or out of the way when it has no window to go back to.</summary>
    private async Task BackToGameFromButtonAsync(GameSession session)
    {
        if (await ReturnToGameAsync(session))
        {
            Program.Log($"Back to {session.Game.Key} from the Armoury Crate button");
            return;
        }

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
