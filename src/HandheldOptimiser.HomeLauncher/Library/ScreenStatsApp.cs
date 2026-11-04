using System.Windows;
using System.Windows.Threading;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Runs the stats screen: the launcher started by TDP Engine with --screen-stats while the switch is on in
/// the widget. One at a time. While a monitor is in use it turns the Ally's panel on beside it (see
/// <see cref="PanelKeeper"/>) and fills it with the time and big readings, asked of TDP Engine each second as
/// the overlay does. Other windows and the mouse are kept off the panel (see <see cref="PanelGuard"/>). Undocked it shows nothing and waits. It ends, putting the screens back, when switched off
/// or when TDP Engine has not answered for a few seconds, as when the service stops.
/// </summary>
internal static class ScreenStatsApp
{
    /// <summary>
    /// The single instance mutex, in the player's session. TDP Engine looks for it to tell whether the stats
    /// screen is running; its copy is ScreenStatsHost.MutexName in the service.
    /// </summary>
    internal const string MutexName = "HandheldOptimiser.ScreenStats";

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    // Seconds without an answer before it gives up.
    private const int MissesToEnd = 5;

    // Seconds undocked before it closes, so a monitor that drops for a moment as it wakes does not end it.
    // TDP Engine starts it again once a monitor is plugged in.
    private const int UndockedToEnd = 5;

    public static void Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        // Text redrawn once a second needs no GPU, which is the game's.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Program.Log($"Unhandled error in the stats screen: {e.Exception}");
            e.Handled = true;
        };

        var panel = new PanelKeeper();
        using var guard = new PanelGuard();
        using var readings = new OverlayReadings();
        var fps = new FpsAverage();
        var window = new ScreenStatsWindow();
        var timer = new DispatcherTimer { Interval = Every };
        var misses = 0;
        var busy = false;
        var hidden = false;
        var undocked = 0;

        // Hidden while locked, or while a monitor is plugged in but the panel cannot be had, it can wait for
        // hours, so its memory is handed back once as it hides, as the library does behind a game. The next
        // reading after that brings back only what it uses.
        void Hide()
        {
            guard.Stop();
            window.HideAway();
            if (!hidden)
            {
                hidden = true;
                Native.TrimWorkingSet();
            }
        }

        // Switched off by the player, the Ally's screen goes off with it. Ended any other way, only what was
        // changed here is put back.
        void End(string why, bool switchedOff = false)
        {
            timer.Stop();
            Program.Log($"Stats screen closed: {why}");
            window.Close();
            guard.Stop();
            if (switchedOff)
            {
                panel.SwitchOff();
            }
            else
            {
                panel.PutBack();
            }

            app.Shutdown();
        }

        timer.Tick += async (_, _) =>
        {
            // A slow answer is not stacked on by the next second's question.
            if (busy)
            {
                return;
            }

            busy = true;
            try
            {
                var process = OverlayReadings.ForegroundProcess();
                var status = await AutoTdp.OverlayAsync(process);
                if (status is null)
                {
                    if (++misses >= MissesToEnd)
                    {
                        End("TDP Engine stopped answering");
                    }

                    return;
                }

                misses = 0;
                if (!status.ScreenStats)
                {
                    End("switched off", switchedOff: true);
                    return;
                }

                // Locked, the Ally's screen is turned off, and turned on again once unlocked.
                if (SessionLock.IsLocked())
                {
                    Hide();
                    panel.TurnOffWhileLocked();
                    return;
                }

                if (panel.Keep() is not { } bounds)
                {
                    Hide();

                    // The same test TDP Engine starts it by, so the two never take turns starting and ending it.
                    if (Monitors.Docked())
                    {
                        undocked = 0;
                    }
                    else if (++undocked >= UndockedToEnd)
                    {
                        End("undocked");
                    }

                    return;
                }

                hidden = false;
                undocked = 0;

                var reading = readings.Read(status) with { Fps = fps.Add(process, status.Fps) };
                window.ShowReadings(ScreenStatsLayout.Tiles(reading), DateTime.Now);
                window.ShowOn(bounds);
                guard.Keep(bounds);
            }
            finally
            {
                busy = false;
            }
        };

        app.Startup += (_, _) => timer.Start();

        // Signing out or shutting down: the screens are put back before Windows ends this program.
        app.SessionEnding += (_, _) => End("signing out or shutting down");
        Program.Log("Stats screen started");
        try
        {
            app.Run();
        }
        finally
        {
            // Whatever ended it, the player gets their screens back.
            panel.PutBack();
        }
    }
}
