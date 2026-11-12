using System.IO;
using System.Text;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One non-Steam game shortcut from a Steam user's config\shortcuts.vdf.</summary>
internal sealed record SteamShortcut(
    uint AppId,
    string Name,
    string Executable,
    string? Arguments,
    string? WorkingDirectory,
    string? IconPath);

/// <summary>
/// Non-Steam games the player added to Steam ("Add a Non-Steam Game to My Library"), stored in each
/// Steam account's userdata\&lt;id&gt;\config\shortcuts.vdf in Valve's binary VDF format, with custom
/// artwork in userdata\&lt;id&gt;\config\grid\. Scanned after the stores so a game already in the library
/// from its own store is not listed twice.
/// </summary>
internal static class SteamShortcuts
{
    public const string KeyPrefix = "steam:shortcut:";

    private static readonly string[] CoverExtensions = ["p.png", "p.jpg"];
    private static readonly string[] HeroExtensions = ["_hero.png", "_hero.jpg", ".png", ".jpg"];
    private static readonly string[] IconExtensions = ["_icon.png", "_icon.ico"];

    public static bool IsShortcut(Game game) =>
        game.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<Game> Scan()
    {
        if (SteamLibrary.SteamDirectory() is not { } steamDir)
        {
            yield break;
        }

        var userdata = Path.Combine(steamDir, "userdata");
        string[] accounts;
        try
        {
            if (!Directory.Exists(userdata))
            {
                yield break;
            }

            accounts = Directory.GetDirectories(userdata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var seen = new HashSet<uint>();
        foreach (var account in accounts)
        {
            var configDir = Path.Combine(account, "config");
            var vdfPath = Path.Combine(configDir, "shortcuts.vdf");
            byte[] bytes;
            try
            {
                if (!File.Exists(vdfPath))
                {
                    continue;
                }

                bytes = File.ReadAllBytes(vdfPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var gridDir = Path.Combine(configDir, "grid");
            foreach (var shortcut in ParseShortcuts(bytes))
            {
                if (!seen.Add(shortcut.AppId) || !File.Exists(shortcut.Executable))
                {
                    continue;
                }

                var cover = FindGridArt(gridDir, shortcut.AppId, CoverExtensions);
                var hero = FindGridArt(gridDir, shortcut.AppId, HeroExtensions);
                var icon = FindGridArt(gridDir, shortcut.AppId, IconExtensions)
                    ?? (shortcut.IconPath is { } customIcon && File.Exists(customIcon) ? customIcon : shortcut.Executable);
                var installDir = Path.GetDirectoryName(shortcut.Executable);
                var workingDir = shortcut.WorkingDirectory is { } dir && Directory.Exists(dir) ? dir : installDir;

                yield return new Game(
                    Key: $"{KeyPrefix}{shortcut.AppId}",
                    Title: shortcut.Name,
                    Store: GameStore.Steam,
                    CoverPath: cover,
                    IconPath: cover is null ? icon : null,
                    LaunchTarget: shortcut.Executable,
                    LaunchArguments: shortcut.Arguments,
                    WorkingDirectory: workingDir,
                    InstallDirectory: installDir,
                    ExecutablePath: shortcut.Executable,
                    HeroPath: hero);
            }
        }
    }

    /// <summary>Parses the shortcuts in a binary shortcuts.vdf file, skipping hidden or incomplete entries.</summary>
    public static List<SteamShortcut> ParseShortcuts(byte[] data)
    {
        var shortcuts = new List<SteamShortcut>();
        if (data.Length == 0)
        {
            return shortcuts;
        }

        try
        {
            var position = 0;
            var root = SteamAppInfo.ReadObject(data, ref position, data.Length, names: null, depth: 0);
            var list = root.GetValueOrDefault("shortcuts") as Dictionary<string, object>
                ?? root.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
            if (list is null)
            {
                return shortcuts;
            }

            foreach (var entry in list.Values.OfType<Dictionary<string, object>>())
            {
                if (entry.GetValueOrDefault("IsHidden") is int hidden && hidden != 0)
                {
                    continue;
                }

                var name = (entry.GetValueOrDefault("AppName") as string)?.Trim();
                var rawExe = (entry.GetValueOrDefault("Exe") as string)?.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(rawExe))
                {
                    continue;
                }

                var launchOptions = entry.GetValueOrDefault("LaunchOptions") as string;
                var (exe, arguments) = SplitTarget(rawExe, launchOptions);
                if (exe.Length == 0)
                {
                    continue;
                }

                var appId = entry.GetValueOrDefault("appid") is int id && id != 0
                    ? unchecked((uint)id)
                    : ComputeAppId(rawExe, name);
                var startDir = Unquote(entry.GetValueOrDefault("StartDir") as string);
                var icon = Unquote(entry.GetValueOrDefault("icon") as string);

                shortcuts.Add(new SteamShortcut(
                    appId,
                    name,
                    exe,
                    arguments,
                    startDir.Length > 0 ? startDir : null,
                    icon.Length > 0 ? icon : null));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            return [];
        }

        return shortcuts;
    }

    /// <summary>
    /// Splits Steam's Exe field (which is usually quoted and may carry arguments after the closing quote)
    /// and combines any inline arguments with LaunchOptions.
    /// </summary>
    public static (string Executable, string? Arguments) SplitTarget(string rawExe, string? launchOptions)
    {
        var trimmed = rawExe.Trim();
        string exe;
        var inlineArgs = string.Empty;

        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 1)
            {
                exe = trimmed[1..close].Trim();
                inlineArgs = trimmed[(close + 1)..].Trim();
            }
            else
            {
                exe = trimmed.Trim('"').Trim();
            }
        }
        else
        {
            exe = trimmed;
        }

        var extra = launchOptions?.Trim() ?? string.Empty;
        var combined = inlineArgs.Length > 0 && extra.Length > 0 ? $"{inlineArgs} {extra}"
            : inlineArgs.Length > 0 ? inlineArgs
            : extra;

        return (exe, combined.Length > 0 ? combined : null);
    }

    /// <summary>Steam's shortcut appid when the entry has none: CRC32 of (Exe + AppName) with the top bit set.</summary>
    public static uint ComputeAppId(string rawExe, string appName)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(rawExe + appName))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc | 0x80000000u;
    }

    private static string Unquote(string? value) =>
        value is null ? string.Empty : value.Trim().Trim('"').Trim();

    private static string? FindGridArt(string gridDir, uint appId, string[] suffixes)
    {
        try
        {
            if (!Directory.Exists(gridDir))
            {
                return null;
            }

            foreach (var suffix in suffixes)
            {
                var path = Path.Combine(gridDir, $"{appId}{suffix}");
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
