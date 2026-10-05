using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads installed Blizzard games from their uninstall entries, which carry the game's Battle.net uid.
/// Blizzard games only run through the Battle.net app, so they are started with its --exec="launch CODE"
/// switch. A game whose code is not known here opens the Battle.net app instead.
/// </summary>
internal static partial class BattleNetLibrary
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Uninstall uid to Battle.net launch product code.
    private static readonly Dictionary<string, string> LaunchCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fenris"] = "Fen",
        ["prometheus"] = "Pro",
        ["wow"] = "WoW",
        ["diablo3"] = "D3",
        ["osi"] = "OSI",
        ["hs_beta"] = "WTCG",
        ["heroes"] = "Hero",
        ["s1"] = "S1",
        ["s2"] = "S2",
        ["w3"] = "W3",
        ["anbs"] = "ANBS",
        ["odin"] = "ODIN",
        ["viper"] = "VIPR",
        ["lazarus"] = "LAZR",
        ["zeus"] = "ZEUS",
        ["fore"] = "FORE"
    };

    public static IEnumerable<Game> Scan()
    {
        var entries = ReadBlizzardEntries().ToList();

        var client = entries.FirstOrDefault(e => string.Equals(e.Uid, "battle.net", StringComparison.OrdinalIgnoreCase));
        var clientExe = client is null ? null : Path.Combine(client.InstallLocation, "Battle.net.exe");
        if (clientExe is null || !File.Exists(clientExe))
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry == client)
            {
                continue;
            }

            var hasCode = LaunchCodes.TryGetValue(entry.Uid, out var code);
            yield return new Game(
                Key: $"battlenet:{entry.Uid}",
                Title: entry.DisplayName,
                Store: GameStore.BattleNet,
                CoverPath: null,
                IconPath: entry.DisplayIcon,
                LaunchTarget: clientExe,
                LaunchArguments: hasCode ? $"--exec=\"launch {code}\"" : null,
                InstallDirectory: entry.InstallLocation,
                // Blizzard points the uninstall entry's icon at the game's launcher exe.
                ExecutablePath: entry.DisplayIcon is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? icon : null);
        }
    }

    private sealed record Entry(string Uid, string DisplayName, string InstallLocation, string? DisplayIcon);

    private static IEnumerable<Entry> ReadBlizzardEntries()
    {
        // Battle.net is 32-bit and writes its entries (and its games') to the 32-bit view.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var uninstall = hklm.OpenSubKey(UninstallKey);
        if (uninstall is null)
        {
            yield break;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key?.GetValue("Publisher") is not string publisher
                || !publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)
                || key.GetValue("UninstallString") is not string uninstallString
                || key.GetValue("DisplayName") is not string displayName
                || key.GetValue("InstallLocation") is not string location)
            {
                continue;
            }

            var uid = UidArgument().Match(uninstallString);
            if (!uid.Success)
            {
                continue;
            }

            var icon = (key.GetValue("DisplayIcon") as string)?.Trim('"');
            yield return new Entry(uid.Groups[1].Value, displayName, location, icon);
        }
    }

    [GeneratedRegex(@"--uid=(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex UidArgument();
}
