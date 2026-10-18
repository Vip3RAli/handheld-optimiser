using System.Diagnostics;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Command Center button opens Game Bar, which is worked with the controller as a gamepad. In Desktop
/// mode the controller is a mouse and keyboard, so it is made a gamepad for as long as Game Bar is open,
/// as Armoury Crate did, and then put back. The change is never saved, so sleep or a restart in between
/// still brings back the mode the player chose.
/// </summary>
internal static class GameBarControls
{
    // How long Game Bar has to come up after Win+G, and the longest it is kept a gamepad for.
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongestOpen = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>The mode to hold while Game Bar is open, or null to leave the controller as it is.</summary>
    public static string? ModeWhileOpen(AutoTdpStatus? status) =>
        status is { Replacing: true, ControllerMode: AutoTdpProtocol.DesktopMode } ? AutoTdpProtocol.GamepadMode : null;

    /// <summary>Opens Game Bar with Win+G, holding the controller as a gamepad until it closes when need be.</summary>
    /// <returns>False when Windows would not take Win+G.</returns>
    public static bool Open()
    {
        var hold = ModeWhileOpen(AutoTdp.StatusAsync().GetAwaiter().GetResult());
        if (hold is not null)
        {
            _ = AutoTdp.SetControllerModeAsync(hold, temporary: true).GetAwaiter().GetResult();
        }

        var opened = Native.PressWinG();
        if (hold is null)
        {
            return opened;
        }

        if (opened)
        {
            WaitWhileOpen();
        }

        // TDP Engine puts back the mode saved now, which the player may have changed in the widget meanwhile.
        _ = AutoTdp.ReleaseControllerModeAsync().GetAwaiter().GetResult();
        Program.Log("Game Bar closed, so the controller is back in the mode the player chose");
        return opened;
    }

    /// <summary>
    /// Waits for Game Bar to open, then for it to close. Game Bar is never the window in front, so this asks
    /// Windows whether Game Bar has the input (see GameBarState). If Windows will not say, it stops waiting.
    /// </summary>
    private static void WaitWhileOpen()
    {
        var started = Stopwatch.StartNew();
        while (GameBarState.IsOpen() == false && started.Elapsed < OpenWait)
        {
            Thread.Sleep(Poll);
        }

        while (GameBarState.IsOpen() == true && started.Elapsed < LongestOpen)
        {
            Thread.Sleep(Poll);
        }
    }
}
