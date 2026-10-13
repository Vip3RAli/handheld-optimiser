using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace HandheldOptimiser.Services;

/// <summary>
/// Opens a Windows Settings or Store page from this elevated app without the page running as
/// administrator: the link is handed to Explorer, which opens it in the user's normal session.
/// </summary>
public static class ShellLinks
{
    /// <summary>The only kinds of link this opens. Anything else would be Explorer running a file or a program.</summary>
    private static readonly string[] AllowedSchemes = ["ms-settings:", "ms-windows-store:"];

    public static bool IsAllowed(string uri) =>
        AllowedSchemes.Any(scheme => uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns false, with the reason logged, when the link was refused or could not be opened.</summary>
    public static bool Open(string uri, LogService log)
    {
        if (!IsAllowed(uri))
        {
            log.Error($"Not opening {uri}: only Settings and Store pages are opened.");
            return false;
        }

        try
        {
            var explorer = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false
            };
            explorer.ArgumentList.Add(uri);
            Process.Start(explorer);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Error($"Could not open {uri}: {ex.Message}");
            return false;
        }
    }
}
