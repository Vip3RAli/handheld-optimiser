using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Runs the game library as a single instance. Windows starts the home app again on every home button
/// press; a second start just tells the running library to come to the front and exits.
/// </summary>
internal static class LibraryApp
{
    private const string ShowEventName = @"Local\HandheldOptimiser.Library.Show";

    /// <param name="startedByService">
    /// True when started with --library --from-button, as the Armoury Crate button's service does. A program
    /// started by a service does not own the foreground, so it takes it with a synthetic key press. Every other
    /// start (the home button, a shortcut) already has the foreground and is left as it was.
    /// </param>
    public static int Run(bool startedByService = false)
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out var createdNew);

        if (!createdNew)
        {
            // This process got the foreground from the home button press; pass that right on to the library.
            if (startedByService)
            {
                Native.TakeForegroundRights();
            }

            Native.AllowSetForegroundWindow(Native.AsfwAny);
            showEvent.Set();
            return 0;
        }

        // A game the last library paused and never resumed, if it ended without doing so.
        GamePause.ResumeLeftOver();

        // A static tile grid does not need the GPU. Measured on the Ally, software rendering halves the
        // library's private memory (about 100 MB to 50 MB) and keeps the graphics driver out of the process.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        // The library stays open for hours behind games. One bad picture, file or reply from a store
        // should cost that one thing, not take the home screen away; it is logged and the library goes on.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Program.Log($"Unhandled error in the game library: {e.Exception}");
            e.Handled = !IsFatal(e.Exception);
        };

        // A background task nobody awaited that failed: logged, and kept from being raised again later.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // An error on another thread cannot be stopped from ending the process, but it is at least logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Program.Log($"Game library ending on an unhandled error: {e.ExceptionObject}");

        LibraryWindow? window = null;
        RegisteredWaitHandle? registration = null;
        try
        {
            window = new LibraryWindow();
            var shown = window;
            registration = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (_, _) => app.Dispatcher.BeginInvoke(shown.ShowLibrary),
                null, Timeout.Infinite, executeOnlyOnce: false);

            Program.Log("Game library started");

            // Just before the window is shown, so the library comes to the front rather than flashing.
            if (startedByService)
            {
                Native.TakeForegroundRights();
            }

            return app.Run(window);
        }
        finally
        {
            registration?.Unregister(null);
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

            // The window puts back a game's profile and programs as it closes. If the library failed
            // before it could close, that is done here instead; EndSession only ever runs once.
            if (window is not null)
            {
                try
                {
                    window.EndSession();
                }
                catch (Exception ex)
                {
                    Program.Log($"Could not put back the game's settings as the library ended: {ex}");
                }
            }

            // The window resumes a paused game as it closes; this covers the library failing instead.
            GamePause.ResumeLeftOver();
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Program.Log($"Unobserved background error in the game library: {e.Exception}");
        e.SetObserved();
    }

    /// <summary>
    /// Errors that leave the process in no state to carry on, which are let through to end it; the
    /// launcher then falls back to the Xbox app.
    /// </summary>
    internal static bool IsFatal(Exception ex) => ex is OutOfMemoryException or AccessViolationException
        or System.Runtime.InteropServices.SEHException or InvalidProgramException or BadImageFormatException
        || (ex.InnerException is { } inner && IsFatal(inner));
}
