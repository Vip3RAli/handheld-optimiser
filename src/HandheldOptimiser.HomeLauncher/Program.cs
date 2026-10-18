using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher;

/// <summary>What the launcher was asked to open.</summary>
internal enum LaunchAction
{
    Home,
    Library,
    GameBar
}

/// <summary>
/// Windows starts this as the full screen experience home app: at sign-in when "enter full screen mode
/// at startup" is on, and whenever the home button is pressed. It opens the app chosen in Handheld
/// Optimiser and exits. Opening an app that is already running just brings it to the front, so the home
/// button always lands back on it. The game library is the exception: it stays running, see LibraryApp.
/// </summary>
internal static class Program
{
    // Written by the main app; see HomeAppSettings there. Per user, like the rest of the FSE settings.
    internal const string SettingsKey = @"Software\HandheldOptimiser\HomeApp";

    private const string SteamBigPicture = "steam://open/bigpicture";
    private const string ArmouryCrateSe = @"shell:AppsFolder\B9ECED6F.ArmouryCrateSE_qmba6cd70vzyy!App";
    private const string XboxApp = @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "logs", "home-launcher.log");

    [STAThread]
    private static int Main(string[] args)
    {
        var action = ActionFor(args);

        // Game Bar is opened whatever the home app choice is, and the choice is not read or changed.
        if (action == LaunchAction.GameBar)
        {
            OpenGameBar();
            return 0;
        }

        var (target, customPath, customArgs) = ReadSettings();

        // Opens the library whatever the saved choice, for a desktop shortcut, for testing, or when the
        // Armoury Crate button is pressed.
        if (action == LaunchAction.Library)
        {
            target = "library";
        }

        // Only TDP Engine's Armoury Crate button says --from-button. A program the service starts does not
        // own the foreground and has to take it; anything else that opens the library already has it.
        var takeForeground = action == LaunchAction.Library && IsFromButton(args);

        var launched = target switch
        {
            "library" => RunLibrary(takeForeground),
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

    /// <summary>Runs until the library window closes. A crash falls back to the Xbox app like a failed open.</summary>
    private static bool RunLibrary(bool takeForeground)
    {
        try
        {
            Library.LibraryApp.Run(takeForeground);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Game library failed: {ex}");
            return false;
        }
    }

    /// <summary>What the command line asks for: --library, --gamebar, or neither (the saved home app).</summary>
    internal static LaunchAction ActionFor(string[] args)
    {
        if (args.Contains("--library", StringComparer.OrdinalIgnoreCase))
        {
            return LaunchAction.Library;
        }

        return args.Contains("--gamebar", StringComparer.OrdinalIgnoreCase) ? LaunchAction.GameBar : LaunchAction.Home;
    }

    /// <summary>
    /// The argument TDP Engine adds when the Armoury Crate button opens the library. The service's copy is in
    /// src\HandheldOptimiser.PowerService\AllyControlsHost.cs, and the two must be kept in step.
    /// </summary>
    internal const string FromButtonArgument = "--from-button";

    /// <summary>Whether TDP Engine started this for the Armoury Crate button, with <see cref="FromButtonArgument"/>.</summary>
    internal static bool IsFromButton(string[] args) => args.Contains(FromButtonArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens Xbox Game Bar, which is what the Command Center button shows.</summary>
    private static void OpenGameBar()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-gamebar:") { UseShellExecute = true })?.Dispose();
            Log("Opened Game Bar");
        }
        catch (Win32Exception ex)
        {
            Log($"Failed to open Game Bar: {ex.Message}");
        }
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

    internal static void Log(string message)
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
