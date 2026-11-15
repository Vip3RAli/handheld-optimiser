using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Handing an install to the Epic Games Launcher so that it takes. Left running since the device started,
/// the launcher finds a game it has never looked up "not owned" in its own cache, fails with an alert that
/// has no message (AI-NE), and then ignores the link for that game until it is exited, saying it "is already
/// being processed". All the player sees is the launcher's home page, or nothing. Seen three times on this
/// Ally on 2026-11-15; exiting the launcher and pressing again worked each time, so that is what is done
/// here, as the user asked: the launcher is closed first and the link starts it afresh. Downloads it had
/// under way carry on once it is back. The launcher's own log is the only place it says how the link went.
/// </summary>
internal static class EpicLauncherLog
{
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Logs", "EpicGamesLauncher.log");

    /// <summary>How long the launcher's log is now, to read only what it writes after a link is sent.</summary>
    public static long Length()
    {
        try
        {
            return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Whether what the launcher wrote says it refused an install link.</summary>
    internal static bool Refused(string written) =>
        written.Contains("Reporting Alert code: AI-NE", StringComparison.Ordinal)
        || written.Contains("is already being processed by FAppInstallUriHandler", StringComparison.Ordinal);

    /// <summary>Whether what the launcher wrote says it took an install link and is showing its install page.</summary>
    internal static bool Accepted(string written) =>
        !Refused(written) && written.Contains("AppInstallUriHandler: Dispatching install", StringComparison.Ordinal);

    /// <summary>
    /// Opens the launcher's install page for a game, closing the launcher first so that it starts afresh, and
    /// once more if it still refuses. Tells the player when it would not, as the library comes back in front.
    /// </summary>
    /// <param name="restart">False while a game is being played, which a launcher closed under it could disturb.</param>
    public static async Task InstallAsync(Game game, NoticePresenter notices, bool restart)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (restart && await Task.Run(Exit))
            {
                Program.Log("Closed the Epic launcher so that it takes the install link afresh");
            }

            var from = Length();
            if (GameCatalog.Launch(game) is { } problem)
            {
                notices.ShowWhenInFront(problem);
                return;
            }

            if (!await RefusedAsync(from))
            {
                return;
            }

            Program.Log("The Epic launcher refused the install link (AI-NE or already being processed)");
            if (!restart)
            {
                break;
            }
        }

        notices.ShowWhenInFront($"The Epic launcher would not open {game.Title}'s install page. Exit the launcher from the system tray, then try again.");
    }

    /// <summary>Closes the launcher. True when it was running.</summary>
    private static bool Exit()
    {
        var processes = Process.GetProcessesByName("EpicGamesLauncher");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    Program.Log($"Could not close the Epic launcher: {ex.Message}");
                }
            }

            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Follows the launcher's log after a link was sent, for as long as a launcher starting from nothing
    /// takes to get to it. True when the launcher refused the link; false when it took it, or said nothing.
    /// </summary>
    private static async Task<bool> RefusedAsync(long from)
    {
        var accepted = 0;
        for (var look = 0; look < 75; look++)
        {
            await Task.Delay(1000);
            var written = await Task.Run(() => ReadFrom(from));
            if (Refused(written))
            {
                return true;
            }

            // The refusal follows the dispatch at once, so a dispatch still standing a few looks later took.
            if (Accepted(written) && ++accepted >= 3)
            {
                return false;
            }
        }

        return false;
    }

    private static string ReadFrom(long from)
    {
        try
        {
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // A log shorter than it was is a new one, started as the launcher did.
            stream.Position = stream.Length >= from ? from : 0;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
