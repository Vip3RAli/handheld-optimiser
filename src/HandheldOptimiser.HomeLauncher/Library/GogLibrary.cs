using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed GOG games from the registry entries GOG Galaxy and the offline installers write. Most
/// GOG games are DRM-free, so they are started straight from their exe with no client running.
/// </summary>
internal static class GogLibrary
{
    private const string GamesKey = @"SOFTWARE\GOG.com\Games";

    public static IEnumerable<Game> Scan()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var games = hklm.OpenSubKey(GamesKey);
        if (games is null)
        {
            yield break;
        }

        foreach (var id in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(id);
            if (key is null)
            {
                continue;
            }

            // DLC and add-ons (such as Phantom Liberty) depend on a base game and have no exe of their own.
            if (key.GetValue("dependsOn") is string { Length: > 0 }
                || key.GetValue("exe") is not string { Length: > 0 } exe
                || key.GetValue("gameName") is not string { Length: > 0 } title
                || !File.Exists(exe))
            {
                continue;
            }

            var installDir = key.GetValue("path") as string;
            var workingDir = key.GetValue("workingDir") as string is { Length: > 0 } wd ? wd : Path.GetDirectoryName(exe);
            var gogIcon = installDir is null ? null : Path.Combine(installDir, $"goggame-{id}.ico");

            yield return new Game(
                Key: $"gog:{id}",
                Title: title,
                Store: GameStore.Gog,
                CoverPath: null,
                IconPath: gogIcon is not null && File.Exists(gogIcon) ? gogIcon : exe,
                LaunchTarget: exe,
                LaunchArguments: key.GetValue("launchParam") as string,
                WorkingDirectory: workingDir,
                InstallDirectory: installDir ?? Path.GetDirectoryName(exe),
                ExecutablePath: exe);
        }
    }
}
