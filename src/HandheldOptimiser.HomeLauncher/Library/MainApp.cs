using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The main Handheld Optimiser app, installed beside the library. It runs as administrator and the
/// library does not, so it is started through the shell, which shows the administrator prompt exactly
/// as the Start menu would.
/// </summary>
internal static class MainApp
{
    private const string ExeName = "HandheldOptimiser.exe";

    // The same name is registered by the main app's window; see MainWindow.OnSourceInitialized there.
    private const string ShowMessageName = "HandheldOptimiser.ShowMainWindow";

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, ExeName);

    /// <summary>
    /// Brings the main app to the front if it is already open, so opening it from the library does not
    /// start a second copy or ask for administrator permission again. Matched by the exe's full path,
    /// not its name alone, so some other program called HandheldOptimiser.exe is not mistaken for it.
    /// </summary>
    /// <returns>True when it was already open, whether or not Windows let it come to the front.</returns>
    public static bool ShowIfRunning()
    {
        var found = false;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                if (found || !string.Equals(Native.ProcessPath(process.Id), ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;

                // No window yet means it is still starting, and will come to the front by itself.
                if (process.MainWindowHandle != 0)
                {
                    Native.BringToFront(process.MainWindowHandle, process.Id, ShowMessageName);
                }
            }
        }

        return found;
    }

    /// <returns>What went wrong, or null when the main app was started.</returns>
    public static string? Start(string? arguments = null)
    {
        var mainApp = ExePath;
        if (!File.Exists(mainApp))
        {
            return "Handheld Optimiser was not found beside the library.";
        }

        try
        {
            Process.Start(new ProcessStartInfo(mainApp)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            })?.Dispose();

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Declining the administrator prompt lands here too.
            return ex.Message;
        }
    }
}
