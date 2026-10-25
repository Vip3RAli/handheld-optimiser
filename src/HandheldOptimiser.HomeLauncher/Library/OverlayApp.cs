using System.Windows;
using System.Windows.Threading;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Runs the performance overlay: the launcher started by TDP Engine with --overlay. One at a time. Each
/// second it asks TDP Engine for the frame rate of the program in front and the level and corner chosen in
/// the widget, takes its own readings, and redraws. It ends when the level goes to Off, or when TDP Engine
/// has not answered for a few seconds, as when the service stops.
/// </summary>
internal static class OverlayApp
{
    /// <summary>
    /// The single instance mutex, in the player's session. TDP Engine looks for it to tell whether the
    /// overlay is running; its copy is OverlayHost.MutexName in the service.
    /// </summary>
    internal const string MutexName = "HandheldOptimiser.Overlay";

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    // Seconds without an answer before it gives up.
    private const int MissesToEnd = 5;

    public static void Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        // A few rows of text need no GPU, and this keeps the overlay's memory down, as for the library.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Program.Log($"Unhandled error in the overlay: {e.Exception}");
            e.Handled = true;
        };

        using var readings = new OverlayReadings();
        var fps = new FpsAverage();
        var window = new OverlayWindow();
        var misses = 0;
        var busy = false;

        var timer = new DispatcherTimer { Interval = Every };
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
                        Program.Log("Overlay closed: TDP Engine stopped answering");
                        window.Close();
                    }

                    return;
                }

                misses = 0;
                var level = status.OverlayLevel ?? AutoTdpProtocol.OverlayOff;
                if (level == AutoTdpProtocol.OverlayOff)
                {
                    Program.Log("Overlay closed: switched off");
                    window.Close();
                    return;
                }

                // The frame rate shown, and the frame time worked from it, are a few seconds' average.
                var reading = readings.Read(status) with { Fps = fps.Add(process, status.Fps) };
                window.ShowLines(OverlayLayout.Lines(level, reading), status.OverlayCorner ?? AutoTdpProtocol.TopLeft,
                    status.OverlayLayout == AutoTdpProtocol.OverlayHorizontal);
            }
            finally
            {
                busy = false;
            }
        };

        window.Loaded += (_, _) => timer.Start();
        window.Closed += (_, _) => timer.Stop();
        Program.Log("Overlay started");
        app.Run(window);
    }
}
