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
    private const string GameBarProcess = "GameBar";

    // How long Game Bar has to come up after Win+G, and the longest it is kept a gamepad for.
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongestOpen = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>The mode to hold while Game Bar is open, or null to leave the controller as it is.</summary>
    public static string? ModeWhileOpen(AutoTdpStatus? status) =>
        status is { Replacing: true, ControllerMode: AutoTdpProtocol.DesktopMode } ? AutoTdpProtocol.GamepadMode : null;

    /// <summary>
    /// The mode to put back once Game Bar has closed: the one saved now, which the player may have changed
    /// in the widget meanwhile. Desktop when TDP Engine does not say, as that is what was held.
    /// </summary>
    public static string ModeAfterwards(AutoTdpStatus? status) => status?.ControllerMode ?? AutoTdpProtocol.DesktopMode;

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

        var back = ModeAfterwards(AutoTdp.StatusAsync().GetAwaiter().GetResult());
        _ = AutoTdp.SetControllerModeAsync(back, temporary: true).GetAwaiter().GetResult();
        Program.Log($"Game Bar closed, so the controller is back in {back} mode");
        return opened;
    }

    /// <summary>Waits for Game Bar to come to the front, then for it to leave it.</summary>
    private static void WaitWhileOpen()
    {
        var started = Stopwatch.StartNew();
        while (!GameBarInFront() && started.Elapsed < OpenWait)
        {
            Thread.Sleep(Poll);
        }

        while (GameBarInFront() && started.Elapsed < LongestOpen)
        {
            Thread.Sleep(Poll);
        }
    }

    private static bool GameBarInFront()
    {
        var processId = Native.WindowProcess(Native.ForegroundWindow());
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, GameBarProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // It ended between the two calls.
            return false;
        }
    }
}
