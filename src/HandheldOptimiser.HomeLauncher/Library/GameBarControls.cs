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

    /// <summary>
    /// Game Bar's own link for opening it on one widget, here TDP Engine's, as the Command Center button
    /// opened ASUS's widget. The widget's id is its package family, its app's id and its extension's id.
    /// Win+G always opens it on Home.
    /// </summary>
    internal const string WidgetLink = "ms-gamebar://launchForeground/activate/" + AutoTdpProtocol.WidgetPackage + "_App_TdpEngine";

    /// <summary>
    /// Opens Game Bar on the TDP Engine widget, holding the controller as a gamepad until it closes when
    /// need be. Should the link not open it, Win+G opens it on Home.
    /// </summary>
    /// <returns>False when Game Bar could not be opened either way.</returns>
    public static bool Open()
    {
        var hold = ModeWhileOpen(AutoTdp.StatusAsync().GetAwaiter().GetResult());
        if (hold is not null)
        {
            _ = AutoTdp.SetControllerModeAsync(hold, temporary: true).GetAwaiter().GetResult();
        }

        var opened = OpenOnWidget() || Native.PressWinG();
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
    /// Opens Game Bar on the TDP Engine widget with <see cref="WidgetLink"/>. Windows can take the link and
    /// still open nothing, as the plain ms-gamebar: link did on the Ally, so it counts only once Game Bar
    /// has the input, or when Windows will not say.
    /// </summary>
    private static bool OpenOnWidget()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(WidgetLink) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Program.Log($"Game Bar's link to the TDP Engine widget did not open: {ex.Message}");
            return false;
        }

        var started = Stopwatch.StartNew();
        while (started.Elapsed < OpenWait)
        {
            switch (GameBarState.IsOpen())
            {
                case true:
                    Program.Log("Opened Game Bar on the TDP Engine widget");
                    return true;
                case null:
                    return true;
            }

            Thread.Sleep(Poll);
        }

        Program.Log("Game Bar's link to the TDP Engine widget opened nothing, so Win+G is pressed instead");
        return false;
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
