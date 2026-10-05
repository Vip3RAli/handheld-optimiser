using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed Xbox app / PC Game Pass games. Those are packaged apps, told apart from every other
/// packaged app by the MicrosoftGame.config in their install folder, and they are started through the
/// shell's apps folder like a Start menu tile.
/// </summary>
internal static class XboxLibrary
{
    private const string PackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string ConfigName = "MicrosoftGame.config";
    private const string DefaultGamingFolder = "XboxGames";

    public static IEnumerable<Game> Scan()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
        if (packages is null)
        {
            yield break;
        }

        // Package name to its game folder, for the games the registry's own folder does not lead to.
        var gamingRootFolders = GamingRootFolders();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fullName in packages.GetSubKeyNames())
        {
            // Name_Version_Architecture_ResourceId_PublisherId
            var parts = fullName.Split('_');
            if (parts.Length < 5)
            {
                continue;
            }

            using var package = packages.OpenSubKey(fullName);
            var folder = ContentFolder(package?.GetValue("PackageRootFolder") as string)
                ?? gamingRootFolders.GetValueOrDefault(parts[0]);
            if (folder is null)
            {
                continue;
            }

            var family = $"{parts[0]}_{parts[^1]}";
            var game = ReadConfig(folder, family, package?.GetValue("DisplayName") as string);

            // An update in progress leaves two versions of one package registered.
            if (game is not null && seen.Add(game.Key))
            {
                yield return game;
            }
        }
    }

    private static string? ContentFolder(string? folder)
    {
        try
        {
            return folder is not null && File.Exists(Path.Combine(folder, ConfigName)) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static Game? ReadConfig(string folder, string family, string? registryName)
    {
        try
        {
            var config = XDocument.Load(Path.Combine(folder, ConfigName));
            var root = config.Root;
            if (root is null)
            {
                return null;
            }

            var executable = Descendants(root, "Executable").FirstOrDefault();
            var visuals = Descendants(root, "ShellVisuals").FirstOrDefault();

            var title = new[] { visuals?.Attribute("DefaultDisplayName")?.Value, registryName }
                .FirstOrDefault(IsPlainName);
            if (title is null)
            {
                return null;
            }

            var exePath = ExistingFile(folder, executable?.Attribute("Name")?.Value);
            var logo = ExistingFile(folder, visuals?.Attribute("Square150x150Logo")?.Value);

            // The config's application id defaults to "Game" when the executable does not name one.
            var appId = executable?.Attribute("Id")?.Value is { Length: > 0 } id ? id : "Game";

            return new Game(
                Key: $"xbox:{family}",
                Title: title,
                Store: GameStore.Xbox,
                CoverPath: null,
                IconPath: logo ?? exePath,
                LaunchTarget: $@"shell:AppsFolder\{family}!{appId}",
                InstallDirectory: folder,
                ExecutablePath: exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<XElement> Descendants(XElement root, string name) =>
        root.Descendants().Where(e => e.Name.LocalName == name);

    // Names can be resource references ("ms-resource:..." or "@{...}") that only the shell can resolve.
    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("@{", StringComparison.Ordinal);

    private static string? ExistingFile(string folder, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var path = Path.Combine(folder, relative);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Games in each drive's Xbox games folder, by package name. The folder is named by the drive's
    /// .GamingRoot file: "RGBX", a count, then the folder name in UTF-16.
    /// </summary>
    private static Dictionary<string, string> GamingRootFolders()
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (!File.Exists(marker))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(marker);
                var name = bytes.Length > 8 ? Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0') : string.Empty;
                var gamesDir = Path.Combine(drive.RootDirectory.FullName, name.Length > 0 ? name : DefaultGamingFolder);
                if (!Directory.Exists(gamesDir))
                {
                    continue;
                }

                foreach (var gameDir in Directory.GetDirectories(gamesDir))
                {
                    var content = ContentFolder(Path.Combine(gameDir, "Content"));
                    var identity = content is null ? null : IdentityName(content);
                    if (identity is not null)
                    {
                        folders.TryAdd(identity, content!);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // That drive's games are simply not listed.
            }
        }

        return folders;
    }

    private static string? IdentityName(string folder)
    {
        try
        {
            var root = XDocument.Load(Path.Combine(folder, ConfigName)).Root;
            return root is null ? null : Descendants(root, "Identity").FirstOrDefault()?.Attribute("Name")?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}
