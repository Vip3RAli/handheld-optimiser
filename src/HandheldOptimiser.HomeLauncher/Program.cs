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

    /// <summary>
    /// The library's log. The tests point it at a file of their own, since several feed in bad input on
    /// purpose and their lines in the player's log read like faults (2026-10-28).
    /// </summary>
    internal static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "logs", "home-launcher.log");

    [STAThread]
    private static int Main(string[] args)
    {
        // The performance overlay, which TDP Engine starts when it is switched on in the widget.
        if (args.Contains(OverlayArgument, StringComparer.OrdinalIgnoreCase))
        {
            RunOverlay();
            return 0;
        }

        // The stats screen on the Ally's own panel while docked, which TDP Engine starts when it is switched on in the widget.
        if (args.Contains(ScreenStatsArgument, StringComparer.OrdinalIgnoreCase))
        {
            RunScreenStats();
            return 0;
        }

        // The Radeon settings for the widget, which TDP Engine starts as the player, reading what it prints.
        if (args.Length > 0 && string.Equals(args[0], PowerService.RadeonState.Argument, StringComparison.OrdinalIgnoreCase))
        {
            return RunRadeon(args);
        }

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

    /// <summary>
    /// The argument TDP Engine starts the overlay with. The service's copy is in
    /// src\HandheldOptimiser.PowerService\OverlayHost.cs, and the two must be kept in step.
    /// </summary>
    internal const string OverlayArgument = "--overlay";

    private static void RunOverlay()
    {
        try
        {
            Library.OverlayApp.Run();
        }
        catch (Exception ex)
        {
            Log($"Overlay failed: {ex}");
        }
    }

    /// <summary>
    /// The argument TDP Engine starts the stats screen with. The service's copy is in
    /// src\HandheldOptimiser.PowerService\ScreenStatsHost.cs, and the two must be kept in step.
    /// </summary>
    internal const string ScreenStatsArgument = "--screen-stats";

    /// <summary>
    /// --radeon alone reads the Radeon settings; --radeon followed by a setting's name and on, off or a number sets
    /// that one first (see RadeonState). Prints what it found as one line for TDP Engine, which reads this
    /// program's output, and ends with 0, or with 1 when AMD's ADLX could not be used.
    /// </summary>
    private static int RunRadeon(string[] args)
    {
        Library.RadeonSettings.Change? change = null;
        if (args.Length > 1)
        {
            var name = args[1].ToLowerInvariant();
            var value = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            bool? on = value switch { PowerService.RadeonState.OnName => true, PowerService.RadeonState.OffName => false, _ => null };
            int? number = int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
            if (!PowerService.RadeonState.IsChange(name, on, number))
            {
                Log($"Radeon settings: \"{name} {value}\" is not a change this knows");
                return 1;
            }

            Log($"Radeon settings: setting {name} to {value}");
            change = new Library.RadeonSettings.Change(name, on, number);
        }

        if (Library.RadeonSettings.Apply(change) is not { } state)
        {
            return 1;
        }

        // The driver switches some settings off as another goes on (RSR as Image Sharpening does, Anti-Lag as
        // Chill does), and the session that made the change still reads them as they were. A new session reads
        // them as they are (seen on the Ally on 2026-10-28).
        if (change is not null && Library.RadeonSettings.Apply(null) is { } after)
        {
            state = after;
        }

        Console.Out.WriteLine(state.Format());
        Console.Out.Flush();
        return 0;
    }

    private static void RunScreenStats()
    {
        try
        {
            Library.ScreenStatsApp.Run();
        }
        catch (Exception ex)
        {
            Log($"Stats screen failed: {ex}");
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

    /// <summary>
    /// Opens Xbox Game Bar on the TDP Engine widget, which is what the Command Center button shows, with
    /// Win+G when that does not work. The plain ms-gamebar: link opens nothing on the Ally.
    /// </summary>
    private static void OpenGameBar() =>
        Log(Library.GameBarControls.Open() ? "Opened Game Bar" : "Could not open Game Bar: Windows would not take Win+G");

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
