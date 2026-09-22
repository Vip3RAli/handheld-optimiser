using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher;

/// <summary>
/// Windows starts this as the full screen experience home app: at sign-in when "enter full screen mode
/// at startup" is on, and whenever the home button is pressed. It opens the app chosen in Handheld
/// Optimiser and exits. Opening an app that is already running just brings it to the front, so the home
/// button always lands back on it.
/// </summary>
internal static class Program
{
    // Written by the main app; see HomeAppSettings there. Per user, like the rest of the FSE settings.
    private const string SettingsKey = @"Software\HandheldOptimiser\HomeApp";

    private const string SteamBigPicture = "steam://open/bigpicture";
    private const string ArmouryCrateSe = @"shell:AppsFolder\B9ECED6F.ArmouryCrateSE_qmba6cd70vzyy!App";
    private const string XboxApp = @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "logs", "home-launcher.log");

    private static int Main()
    {
        var (target, customPath, customArgs) = ReadSettings();

        var launched = target switch
        {
            "armourycrate" => TryOpen(ArmouryCrateSe),
            "custom" => !string.IsNullOrWhiteSpace(customPath) && TryOpen(customPath, customArgs),
            _ => TryOpen(SteamBigPicture)
        };

        if (launched)
        {
            return 0;
        }

        // A missing or uninstalled target would otherwise leave an empty full screen with no way home.
        Log($"Could not open \"{target}\"; falling back to the Xbox app.");
        return TryOpen(XboxApp) ? 0 : 1;
    }

    private static (string Target, string? CustomPath, string? CustomArgs) ReadSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return (
                (key?.GetValue("Target") as string ?? "steam").ToLowerInvariant(),
                key?.GetValue("CustomPath") as string,
                key?.GetValue("CustomArgs") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log($"Could not read settings ({ex.Message}); using Steam.");
            return ("steam", null, null);
        }
    }

    private static bool TryOpen(string target, string? arguments = null)
    {
        try
        {
            // Shell execute handles URIs, shell:AppsFolder app IDs, shortcuts and plain exes alike.
            Process.Start(new ProcessStartInfo(target)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            })?.Dispose();

            Log($"Opened {target}");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log($"Failed to open {target}: {ex.Message}");
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            // Runs once per home button press; cap the file rather than rotate it.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
            {
                File.Delete(LogPath);
            }

            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never stop the home app from opening.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
