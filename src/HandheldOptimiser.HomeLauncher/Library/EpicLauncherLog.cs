using System.IO;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Whether the Epic Games Launcher took an install link. For a game it has never looked up, the launcher
/// finds it "not owned" in its own cache, fails with an alert that has no message (AI-NE), and then ignores
/// the link for that game until the launcher is exited, saying it "is already being processed". All the
/// player sees is the launcher's home page, or nothing. Seen twice on this Ally on 2026-11-15; exiting the
/// launcher and pressing again worked both times. The launcher's own log is the only place it says so.
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

    /// <summary>
    /// Watches the launcher's log for a few seconds after an install link was sent, and tells the player what
    /// to do if the launcher refused it.
    /// </summary>
    /// <param name="from">The log's length before the link was sent.</param>
    public static async Task TellIfRefusedAsync(long from, string title, NoticePresenter notices)
    {
        for (var look = 0; look < 10; look++)
        {
            await Task.Delay(1000);
            if (Refused(await Task.Run(() => ReadFrom(from))))
            {
                Program.Log("The Epic launcher refused the install link (AI-NE or already being processed)");
                notices.ShowWhenInFront($"The Epic launcher would not open {title}'s install page. Exit the launcher from the system tray, then try again.");
                return;
            }
        }
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
