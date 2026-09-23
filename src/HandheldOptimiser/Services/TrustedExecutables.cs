using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HandheldOptimiser.Services;

/// <summary>
/// Turns the bare tool names this app runs ("dism.exe", "winget.exe") into full paths in folders only
/// administrators can write to, and refuses anything it cannot place there.
///
/// A bare name is looked up the way CreateProcess does it: the current directory and the user's PATH
/// are searched as well as System32. This app runs elevated, and both of those can be written by
/// anything running as the user, so a planted dism.exe would otherwise run with administrator rights.
/// winget is the awkward one: its usual entry point is an alias in %LOCALAPPDATA%\Microsoft\WindowsApps,
/// so it is run from its package folder under Program Files instead.
/// </summary>
public static class TrustedExecutables
{
    public static string PowerShell { get; } =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// The module path given to every PowerShell this app starts. Windows PowerShell otherwise searches
    /// Documents\WindowsPowerShell\Modules first, and a module planted there that defines, say,
    /// Get-AppxPackage would be auto-loaded into an elevated script.
    /// </summary>
    public static string SystemModulePath { get; } = string.Join(';',
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"));

    // Per-user list of installed packages. The user can write to this key, so its names are only
    // trusted once they have been matched against the real package folder under Program Files.
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly Regex WingetPackageFolder = new(
        @"^Microsoft\.DesktopAppInstaller_(?<version>\d+(\.\d+){3})_(x64|arm64|x86|neutral)__8wekyb3d8bbwe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the full path to run for <paramref name="fileName"/>, or null with a reason if it is not in
    /// a trusted location. Paths that are already absolute are the caller's own choice and pass through.
    /// </summary>
    public static string? Resolve(string fileName, out string? reason)
    {
        reason = null;

        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        if (string.Equals(fileName, "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            return PowerShell;
        }

        if (string.Equals(fileName, "winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            var winget = FindWinget();
            if (winget is null)
            {
                reason = "App Installer (winget) was not found in its package folder under Program Files.";
            }

            return winget;
        }

        var system = Path.Combine(Environment.SystemDirectory, fileName);
        if (File.Exists(system))
        {
            return system;
        }

        reason = $"{fileName} is not in {Environment.SystemDirectory}, and is not run from anywhere else.";
        return null;
    }

    private static string? FindWinget()
    {
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

        try
        {
            using var repo = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);

            return (repo?.GetSubKeyNames() ?? [])
                .Select(name => (Name: name, Match: WingetPackageFolder.Match(name)))
                .Where(p => p.Match.Success)
                .OrderByDescending(p => Version.Parse(p.Match.Groups["version"].Value))
                .Select(p => Path.Combine(windowsApps, p.Name, "winget.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
