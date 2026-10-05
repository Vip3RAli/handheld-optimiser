using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed Ubisoft games from the install list Ubisoft Connect keeps in the registry. That list
/// holds only an id and a folder, so the title and icon come from the game's uninstall entry. Ubisoft
/// games only run through Ubisoft Connect, so they are started with its uplay://launch link.
/// </summary>
internal static class UbisoftLibrary
{
    private const string InstallsKey = @"SOFTWARE\Ubisoft\Launcher\Installs";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IEnumerable<Game> Scan()
    {
        // Ubisoft Connect is 32-bit and writes its entries (and its games') to the 32-bit view.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var installs = hklm.OpenSubKey(InstallsKey);
        if (installs is null)
        {
            yield break;
        }

        using var uninstall = hklm.OpenSubKey(UninstallKey);

        foreach (var id in installs.GetSubKeyNames())
        {
            using var key = installs.OpenSubKey(id);
            if (key?.GetValue("InstallDir") is not string { Length: > 0 } recorded)
            {
                continue;
            }

            // Recorded with forward slashes and a trailing one.
            var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded));
            if (!Directory.Exists(installDir))
            {
                continue;
            }

            using var entry = uninstall?.OpenSubKey($"Uplay Install {id}");
            var title = entry?.GetValue("DisplayName") as string is { Length: > 0 } name ? name : Path.GetFileName(installDir);
            var icon = (entry?.GetValue("DisplayIcon") as string)?.Trim('"');

            yield return new Game(
                Key: $"ubisoft:{id}",
                Title: title,
                Store: GameStore.Ubisoft,
                CoverPath: null,
                IconPath: icon is not null && File.Exists(icon) ? icon : null,
                LaunchTarget: $"uplay://launch/{id}/0",
                InstallDirectory: installDir);
        }
    }
}
