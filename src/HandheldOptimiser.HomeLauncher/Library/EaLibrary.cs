using System.IO;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Finds installed EA games. The EA App keeps no single list of them: each game writes an "Install Dir"
/// under its publisher's key, and studios outside those keys (BioWare, Respawn and so on) are only
/// reachable through their uninstall entries. Every folder found either way is then confirmed by the
/// manifest the EA installer leaves in it, which also gives the title, the content id and the exe.
/// Games are started from their own exe, as the EA App's desktop shortcuts are, and the exe brings up
/// the EA App itself.
/// </summary>
internal static class EaLibrary
{
    private static readonly string[] PublisherKeys = [@"SOFTWARE\Electronic Arts", @"SOFTWARE\EA Games"];

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ManifestPath = @"__Installer\installerdata.xml";

    // Publisher keys nest at most a brand deep, such as EA Sports\FC 25.
    private const int MaxDepth = 3;

    public static IEnumerable<Game> Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installDir in InstallDirectories().ToList())
        {
            if (seen.Add(installDir) && ReadGame(installDir) is { } game)
            {
                yield return game;
            }
        }
    }

    private static IEnumerable<string> InstallDirectories()
    {
        // Older games register in the 32-bit view and newer ones in the 64-bit view.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

            foreach (var publisher in PublisherKeys)
            {
                using var key = hklm.OpenSubKey(publisher);
                foreach (var recorded in key is null ? [] : InstallDirValues(key, MaxDepth).ToList())
                {
                    if (Normalise(recorded) is { } dir)
                    {
                        yield return dir;
                    }
                }
            }

            using var uninstall = hklm.OpenSubKey(UninstallKey);
            foreach (var name in uninstall?.GetSubKeyNames() ?? [])
            {
                using var entry = uninstall!.OpenSubKey(name);

                // Every EA App install is removed through the shared EAInstaller folder, whoever published it.
                if (entry?.GetValue("UninstallString") is string uninstallString
                    && uninstallString.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase)
                    && Normalise(entry.GetValue("InstallLocation") as string) is { } dir)
                {
                    yield return dir;
                }
            }
        }
    }

    private static IEnumerable<string> InstallDirValues(RegistryKey key, int depth)
    {
        if (key.GetValue("Install Dir") is string { Length: > 0 } installDir)
        {
            yield return installDir;
        }

        if (depth == 0)
        {
            yield break;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            foreach (var found in child is null ? [] : InstallDirValues(child, depth - 1).ToList())
            {
                yield return found;
            }
        }
    }

    private static string? Normalise(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static Game? ReadGame(string installDir)
    {
        var manifestFile = Path.Combine(installDir, ManifestPath);
        if (!File.Exists(manifestFile))
        {
            return null;
        }

        XDocument manifest;
        try
        {
            manifest = XDocument.Load(manifestFile);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not read {manifestFile}: {ex.Message}");
            return null;
        }

        var contentId = Elements(manifest, "contentID").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        var exe = FindExecutable(manifest, installDir);
        if (exe is null && contentId is null)
        {
            return null;
        }

        return new Game(
            Key: $"ea:{contentId ?? Path.GetFileName(installDir)}",
            Title: FindTitle(manifest) ?? Path.GetFileName(installDir),
            Store: GameStore.Ea,
            CoverPath: null,
            IconPath: exe,
            // A manifest with no usable exe still names the game, so the EA App is asked to start it.
            LaunchTarget: exe ?? $"origin2://game/launch?offerIds={contentId}",
            WorkingDirectory: exe is null ? null : Path.GetDirectoryName(exe),
            InstallDirectory: installDir,
            ExecutablePath: exe);
    }

    private static IEnumerable<XElement> Elements(XContainer container, string name) =>
        container.Descendants().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static string? FindTitle(XDocument manifest)
    {
        var titles = Elements(manifest, "gameTitle").Where(e => !string.IsNullOrWhiteSpace(e.Value)).ToList();
        var title = titles.FirstOrDefault(e => string.Equals((string?)e.Attribute("locale"), "en_US", StringComparison.OrdinalIgnoreCase))
            ?? titles.FirstOrDefault();

        // Trademark signs only get in the way of sorting and artwork lookups.
        var cleaned = title?.Value.Replace("™", string.Empty).Replace("®", string.Empty).Trim();
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    private static string? FindExecutable(XDocument manifest, string installDir)
    {
        return Elements(manifest, "launcher")
            .Where(l => Value(l, "trial") != "1")
            .OrderByDescending(l => Value(l, "requires64BitOS") == "1")
            .Select(l => Resolve(Value(l, "filePath")))
            .FirstOrDefault(path => path is not null && File.Exists(path));

        static string? Value(XElement launcher, string name) => Elements(launcher, name).FirstOrDefault()?.Value.Trim();

        // Written as [HKEY_LOCAL_MACHINE\SOFTWARE\Publisher\Game\Install Dir]folder\game.exe, where the
        // bracketed value is the folder this manifest was found in.
        string? Resolve(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            var relative = filePath[(filePath.LastIndexOf(']') + 1)..].TrimStart('\\', '/');
            try
            {
                return relative.Length == 0 ? null : Path.GetFullPath(Path.Combine(installDir, relative));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
