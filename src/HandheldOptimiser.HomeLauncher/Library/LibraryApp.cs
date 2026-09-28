using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Runs the game library as a single instance. Windows starts the home app again on every home button
/// press; a second start just tells the running library to come to the front and exits.
/// </summary>
internal static class LibraryApp
{
    private const string ShowEventName = @"Local\HandheldOptimiser.Library.Show";

    public static int Run()
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out var createdNew);

        if (!createdNew)
        {
            // This process got the foreground from the home button press; pass that right on to the library.
            Native.AllowSetForegroundWindow(Native.AsfwAny);
            showEvent.Set();
            return 0;
        }

        // A static tile grid does not need the GPU. Measured on the Ally, software rendering halves the
        // library's private memory (about 100 MB to 50 MB) and keeps the graphics driver out of the process.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new LibraryWindow();

        var registration = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => app.Dispatcher.BeginInvoke(window.ShowLibrary),
            null, Timeout.Infinite, executeOnlyOnce: false);

        try
        {
            Program.Log("Game library started");
            return app.Run(window);
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
