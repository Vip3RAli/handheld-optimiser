using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A store's sign-in state on this device: whether an account is signed in, the account name or ID found in
/// its local files, and whether its desktop client is installed.
/// </summary>
internal sealed record StoreAccountStatus(
    GameStore Store,
    string StoreName,
    bool SignedIn,
    string? AccountName,
    string? UserId,
    bool ClientInstalled)
{
    public string Describe() =>
        SignedIn && !string.IsNullOrWhiteSpace(AccountName) ? $"Signed in ({AccountName})"
        : SignedIn ? "Signed in"
        : ClientInstalled ? "Sign in"
        : "Web sign in";
}

/// <summary>
/// Reads local sign-in state for Steam, Epic Games, the EA App (Origin), Ubisoft Connect (Uplay), GOG Galaxy,
/// Xbox and Battle.net, and opens each store's native sign-in window.
/// </summary>
internal static partial class StoreAccounts
{
    private static readonly GameStore[] SupportedStores =
    [
        GameStore.Steam,
        GameStore.Epic,
        GameStore.Ea,
        GameStore.Ubisoft,
        GameStore.Gog,
        GameStore.Xbox,
        GameStore.BattleNet
    ];

    public static List<StoreAccountStatus> ReadAll() =>
        SupportedStores.Select(Read).ToList();

    public static StoreAccountStatus Read(GameStore store) => store switch
    {
        GameStore.Steam => ReadSteam(),
        GameStore.Epic => ReadEpic(),
        GameStore.Ea => ReadEa(),
        GameStore.Ubisoft => ReadUbisoft(),
        GameStore.Gog => ReadGog(),
        GameStore.Xbox => ReadXbox(),
        GameStore.BattleNet => ReadBattleNet(),
        _ => new StoreAccountStatus(store, Game.NameOf(store), false, null, null, false)
    };

    /// <summary>
    /// Opens the store's native client sign-in window when installed, or its web sign-in page when the
    /// client is not on the device.
    /// </summary>
    /// <returns>Null when opened, or a short message when Windows could not open it.</returns>
    public static string? SignIn(GameStore store)
    {
        var (protocolUri, webUri, exePath) = SignInTargets(store);
        var target = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath) ? exePath
            : ClientInstalled(store) ? protocolUri
            : webUri;

        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true
            })?.Dispose();
            Program.Log($"Opened sign-in for {Game.NameOf(store)} ({target})");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Fall back to the web sign-in page if the protocol handler was not registered.
            if (!string.Equals(target, webUri, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(webUri) { UseShellExecute = true })?.Dispose();
                    Program.Log($"Opened web sign-in for {Game.NameOf(store)}");
                    return null;
                }
                catch (Exception inner) when (inner is Win32Exception or InvalidOperationException)
                {
                    Program.Log($"Could not open sign-in for {Game.NameOf(store)}: {inner.Message}");
                }
            }

            return $"Could not open {Game.NameOf(store)} sign-in.";
        }
    }

    private static (string ProtocolUri, string WebUri, string? ExePath) SignInTargets(GameStore store) => store switch
    {
        GameStore.Steam => ("steam://open/main", "https://store.steampowered.com/login/", SteamExePath()),
        GameStore.Epic => ("com.epicgames.launcher://", "https://www.epicgames.com/id/login", null),
        GameStore.Ea => ("origin2://", "https://myaccount.ea.com/", EaExePath()),
        GameStore.Ubisoft => ("uplay://", "https://connect.ubisoft.com/", UbisoftExePath()),
        GameStore.Gog => ("goggalaxy://", "https://www.gog.com/account", GogExePath()),
        GameStore.Xbox => ("xbox://", "https://www.xbox.com/play", null),
        GameStore.BattleNet => ("battlenet://", "https://account.battle.net/", BattleNetExePath()),
        _ => ("https://store.steampowered.com/", "https://store.steampowered.com/", null)
    };

    private static bool ClientInstalled(GameStore store) => store switch
    {
        GameStore.Steam => SteamLibrary.SteamDirectory() is not null,
        GameStore.Epic => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher"))
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher")),
        GameStore.Ea => EaExePath() is not null
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop")),
        GameStore.Ubisoft => UbisoftExePath() is not null
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher")),
        GameStore.Gog => GogExePath() is not null || File.Exists(GogLibrary.DefaultDatabasePath),
        GameStore.Xbox => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.GamingApp_8wekyb3d8bbwe")),
        GameStore.BattleNet => BattleNetExePath() is not null
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net")),
        _ => false
    };

    // ----- Steam -----

    private static StoreAccountStatus ReadSteam()
    {
        var steamPath = SteamLibrary.SteamDirectory();
        var installed = steamPath is not null;
        string? autoLogin = null;
        var activeUser = 0;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            autoLogin = key?.GetValue("AutoLoginUser") as string;
            using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (active?.GetValue("ActiveUser") is int id)
            {
                activeUser = id;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        if (steamPath is not null)
        {
            var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
            try
            {
                if (File.Exists(loginUsers))
                {
                    var parsed = ParseSteamLoginUsers(File.ReadAllText(loginUsers), autoLogin);
                    if (parsed is var (steamId, persona))
                    {
                        return new StoreAccountStatus(GameStore.Steam, "Steam", true, persona, steamId, true);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(autoLogin) || activeUser != 0)
        {
            return new StoreAccountStatus(GameStore.Steam, "Steam", true,
                string.IsNullOrWhiteSpace(autoLogin) ? null : autoLogin,
                activeUser != 0 ? (76561197960265728L + (uint)activeUser).ToString() : null,
                installed);
        }

        return new StoreAccountStatus(GameStore.Steam, "Steam", false, null, null, installed);
    }

    /// <summary>
    /// Reads the active or most recent Steam user from <c>config/loginusers.vdf</c>.
    /// </summary>
    internal static (string SteamId, string Name)? ParseSteamLoginUsers(string vdf, string? preferredAccount = null)
    {
        if (string.IsNullOrWhiteSpace(vdf))
        {
            return null;
        }

        (string SteamId, string Name)? first = null;
        (string SteamId, string Name)? recent = null;

        foreach (Match block in SteamUserBlockPattern().Matches(vdf))
        {
            var steamId = block.Groups[1].Value;
            var body = block.Groups[2].Value;
            var account = VdfValue(body, "AccountName");
            var persona = VdfValue(body, "PersonaName");
            var mostRecent = VdfValue(body, "MostRecent") == "1" || VdfValue(body, "mostrecent") == "1";
            var remember = VdfValue(body, "RememberPassword") == "1";
            var display = !string.IsNullOrWhiteSpace(persona) ? persona
                : !string.IsNullOrWhiteSpace(account) ? account
                : steamId;

            if (!string.IsNullOrWhiteSpace(preferredAccount)
                && string.Equals(account, preferredAccount, StringComparison.OrdinalIgnoreCase))
            {
                return (steamId, display);
            }

            if (mostRecent)
            {
                recent = (steamId, display);
            }
            else if (remember && first is null)
            {
                first = (steamId, display);
            }
        }

        return recent ?? first;
    }

    private static string? VdfValue(string body, string key)
    {
        var match = Regex.Match(body, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex("\"(7656\\d{13})\"\\s*\\{([^}]*)\\}", RegexOptions.Singleline)]
    private static partial Regex SteamUserBlockPattern();

    private static string? SteamExePath() => SteamLibrary.ReadSteamExe();

    // ----- Epic Games -----

    private static StoreAccountStatus ReadEpic()
    {
        var installed = ClientInstalled(GameStore.Epic);
        string? accountId = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\UnrealEngine\Identifiers");
            accountId = (key?.GetValue("AccountId") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var rel in new[]
        {
            Path.Combine("EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini"),
            Path.Combine("EpicGamesLauncher", "Saved", "Config", "WindowsEditor", "GameUserSettings.ini")
        })
        {
            var ini = Path.Combine(local, rel);
            try
            {
                if (File.Exists(ini) && ParseEpicUserSettings(File.ReadAllText(ini)) is var (id, name))
                {
                    return new StoreAccountStatus(GameStore.Epic, "Epic Games", true, name, id ?? accountId, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Heroic's Legendary config also keeps Epic sign-in details when signed in there.
        var heroicUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "heroic", "legendaryConfig", "legendary", "user.json");
        try
        {
            if (File.Exists(heroicUser) && ParseHeroicEpicUser(File.ReadAllText(heroicUser)) is var (id, name))
            {
                return new StoreAccountStatus(GameStore.Epic, "Epic Games", true, name, id ?? accountId, installed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            return new StoreAccountStatus(GameStore.Epic, "Epic Games", true, null, accountId, installed);
        }

        return new StoreAccountStatus(GameStore.Epic, "Epic Games", false, null, null, installed);
    }

    internal static (string? UserId, string? Name)? ParseEpicUserSettings(string iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return null;
        }

        string? userId = null;
        string? name = null;

        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("UserId=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("AccountId=", StringComparison.OrdinalIgnoreCase))
            {
                var val = line[(line.IndexOf('=') + 1)..].Trim();
                if (val.Length > 0)
                {
                    userId = val;
                }
            }
            else if (line.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("LastUser=", StringComparison.OrdinalIgnoreCase))
            {
                var val = line[(line.IndexOf('=') + 1)..].Trim();
                if (val.Length > 0)
                {
                    name = val;
                }
            }
            else if (line.StartsWith("Data=", StringComparison.OrdinalIgnoreCase) && line.Length > 20
                && !line.Contains("False", StringComparison.OrdinalIgnoreCase))
            {
                userId ??= "epic";
            }
        }

        return userId is not null || name is not null ? (userId, name) : null;
    }

    internal static (string? UserId, string? Name)? ParseHeroicEpicUser(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            var id = root.TryGetProperty("account_id", out var a) ? a.GetString() : null;
            return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(id) ? (id, name) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- EA App (Origin) -----

    private static StoreAccountStatus ReadEa()
    {
        var installed = ClientInstalled(GameStore.Ea);
        var eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Electronic Arts", "EA Desktop");

        try
        {
            if (Directory.Exists(eaDir))
            {
                var files = Directory.GetFiles(eaDir, "user_*.ini", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var content = File.ReadAllText(file);
                    if (ParseEaUserIni(Path.GetFileName(file), content) is var (id, name))
                    {
                        return new StoreAccountStatus(GameStore.Ea, "EA App", true, name, id, true);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new StoreAccountStatus(GameStore.Ea, "EA App", false, null, null, installed);
    }

    internal static (string UserId, string? Name)? ParseEaUserIni(string fileName, string? content)
    {
        var match = Regex.Match(fileName, @"^user_([A-Za-z0-9_]+)\.ini$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value;
        string? name = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("user.displayname=", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("personaName=", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("eaid=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
                    if (val.Length > 0)
                    {
                        name = val;
                        break;
                    }
                }
            }
        }

        return (id, name);
    }

    private static string? EaExePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Desktop");
            if (key?.GetValue("ClientPath") is string path && File.Exists(path))
            {
                return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    // ----- Ubisoft Connect (Uplay) -----

    private static StoreAccountStatus ReadUbisoft()
    {
        var installed = ClientInstalled(GameStore.Ubisoft);
        var ubiDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ubisoft Game Launcher");

        try
        {
            if (Directory.Exists(ubiDir))
            {
                var settingsPath = Path.Combine(ubiDir, "settings.yaml");
                var yaml = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
                var spoolDir = Path.Combine(ubiDir, "spool");
                var spoolUsers = Directory.Exists(spoolDir)
                    ? Directory.GetDirectories(spoolDir).Select(Path.GetFileName).OfType<string>().ToArray()
                    : [];

                if (ParseUbisoftAccount(yaml, spoolUsers) is var (id, name))
                {
                    return new StoreAccountStatus(GameStore.Ubisoft, "Ubisoft Connect", true, name, id, true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new StoreAccountStatus(GameStore.Ubisoft, "Ubisoft Connect", false, null, null, installed);
    }

    internal static (string? UserId, string? Name)? ParseUbisoftAccount(string? yaml, IReadOnlyList<string>? spoolUserDirs)
    {
        string? name = null;
        string? userId = spoolUserDirs?.FirstOrDefault(d => Guid.TryParse(d, out _));

        if (!string.IsNullOrWhiteSpace(yaml))
        {
            foreach (var raw in yaml.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("username:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("account_name:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
                    if (val.Length > 0 && !val.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        name ??= val;
                    }
                }
                else if (line.StartsWith("user_id:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("profile_id:", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line[(line.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
                    if (val.Length > 0 && !val.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        userId ??= val;
                    }
                }
            }
        }

        return userId is not null || name is not null ? (userId, name) : null;
    }

    private static string? UbisoftExePath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe");
        return File.Exists(path) ? path : null;
    }

    // ----- GOG Galaxy -----

    private static StoreAccountStatus ReadGog()
    {
        var installed = ClientInstalled(GameStore.Gog);
        var db = GogLibrary.DefaultDatabasePath;
        if (File.Exists(db) && Sqlite.Query(db, "SELECT id, username FROM Users LIMIT 1;", 2) is { Count: > 0 } users)
        {
            var id = users[0][0];
            var name = users[0][1];
            if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name))
            {
                return new StoreAccountStatus(GameStore.Gog, "GOG", true, name, id, true);
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\GOG.com\Galaxy\settings");
            var userId = (key?.GetValue("userId") as string)?.Trim();
            var username = (key?.GetValue("username") as string)?.Trim();
            if (!string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(username))
            {
                return new StoreAccountStatus(GameStore.Gog, "GOG", true, username, userId, installed);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return new StoreAccountStatus(GameStore.Gog, "GOG", false, null, null, installed);
    }

    private static string? GogExePath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "GOG Galaxy", "GalaxyClient.exe");
        return File.Exists(path) ? path : null;
    }

    // ----- Xbox -----

    private static StoreAccountStatus ReadXbox()
    {
        var installed = ClientInstalled(GameStore.Xbox);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\XboxLive");
            var gamertag = (key?.GetValue("Gamertag") as string)?.Trim();
            var xuid = key?.GetValue("Xuid")?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(gamertag) || !string.IsNullOrWhiteSpace(xuid))
            {
                return new StoreAccountStatus(GameStore.Xbox, "Xbox", true, gamertag, xuid, installed);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return new StoreAccountStatus(GameStore.Xbox, "Xbox", false, null, null, installed);
    }

    // ----- Battle.net -----

    private static StoreAccountStatus ReadBattleNet()
    {
        var installed = ClientInstalled(GameStore.BattleNet);
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Battle.net", "Battle.net.config");

        try
        {
            if (File.Exists(configPath) && ParseBattleNetConfig(File.ReadAllText(configPath)) is { } account)
            {
                return new StoreAccountStatus(GameStore.BattleNet, "Battle.net", true, account, account, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new StoreAccountStatus(GameStore.BattleNet, "Battle.net", false, null, null, installed);
    }

    internal static string? ParseBattleNetConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Client", out var client)
                && client.TryGetProperty("SavedAccountNames", out var names)
                && names.GetString() is { Length: > 0 } raw)
            {
                var first = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                return string.IsNullOrWhiteSpace(first) ? null : first;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? BattleNetExePath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Battle.net", "Battle.net Launcher.exe");
        return File.Exists(path) ? path : null;
    }
}
