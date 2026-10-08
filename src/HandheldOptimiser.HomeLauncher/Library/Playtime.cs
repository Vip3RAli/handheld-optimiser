using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>How long a game has been played, and when it was last played.</summary>
internal readonly record struct PlayStats(TimeSpan Played, DateTimeOffset? LastPlayed)
{
    /// <summary>"12 h 5 min played, last played 3 days ago", or null when the game has never been played.</summary>
    public string? Describe(DateTimeOffset now)
    {
        var parts = new List<string>();
        if (Played >= TimeSpan.FromMinutes(1))
        {
            parts.Add($"{Duration(Played)} played");
        }

        if (LastPlayed is { } last)
        {
            parts.Add($"last played {Ago(now - last)}");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string Duration(TimeSpan time) =>
        time.TotalHours >= 100 ? $"{(int)time.TotalHours} h"
        : time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : $"{Math.Max(1, time.Minutes)} min";

    public static string Ago(TimeSpan time) =>
        time < TimeSpan.FromHours(1) ? "just now"
        : time < TimeSpan.FromDays(1) ? "today"
        : time < TimeSpan.FromDays(2) ? "yesterday"
        : time < TimeSpan.FromDays(60) ? $"{(int)time.TotalDays} days ago"
        : time < TimeSpan.FromDays(730) ? $"{(int)(time.TotalDays / 30.4)} months ago"
        : $"{(int)(time.TotalDays / 365.25)} years ago";
}

/// <summary>
/// Play time for every game in the library. The library times the games it starts itself, and for Steam
/// games it also reads Steam's own count, which covers the time before the library and games started
/// from Steam directly.
/// </summary>
internal static class Playtime
{
    // Seconds played, one value per game key.
    private const string PlaytimeKey = Program.SettingsKey + @"\Playtime";

    // Steam's own figures, kept until its file changes. It is several hundred KB on a big library.
    private static readonly object SteamLock = new();
    private static (string Path, DateTime Written, Dictionary<string, PlayStats> Apps)? _steam;

    /// <summary>Every game's play time and last play, for sorting and the quick actions menu.</summary>
    /// <param name="launched">When each game was last started from the library, as unix seconds.</param>
    public static Dictionary<string, PlayStats> Read(IReadOnlyDictionary<string, long> launched)
    {
        var stats = new Dictionary<string, PlayStats>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PlaytimeKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is long seconds && seconds > 0)
                {
                    stats[name] = new PlayStats(TimeSpan.FromSeconds(seconds), null);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No times is a fine fallback.
        }

        foreach (var (name, when) in launched)
        {
            stats[name] = stats.GetValueOrDefault(name) with { LastPlayed = DateTimeOffset.FromUnixTimeSeconds(when) };
        }

        foreach (var (appId, steam) in SteamApps())
        {
            var name = $"steam:{appId}";
            var ours = stats.GetValueOrDefault(name);

            // Steam counts the sessions the library started as well, so the larger figure is the whole.
            stats[name] = new PlayStats(
                steam.Played > ours.Played ? steam.Played : ours.Played,
                Latest(steam.LastPlayed, ours.LastPlayed));
        }

        return stats;
    }

    /// <summary>Adds a finished session to a game's time.</summary>
    public static void Add(Game game, TimeSpan played)
    {
        if (played <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PlaytimeKey);
            var before = key.GetValue(game.Key) as long? ?? 0;
            key.SetValue(game.Key, before + (long)played.TotalSeconds, RegistryValueKind.QWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save the play time of {game.Key}: {ex.Message}");
        }
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Steam's play time and last play for each app id, from the signed-in account's settings.</summary>
    private static Dictionary<string, PlayStats> SteamApps()
    {
        string? path;
        DateTime written;
        try
        {
            path = SteamConfigPath();
            if (path is null)
            {
                return [];
            }

            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        lock (SteamLock)
        {
            if (_steam is { } cached && cached.Path == path && cached.Written == written)
            {
                return cached.Apps;
            }
        }

        var apps = new Dictionary<string, PlayStats>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var config = KeyValues.Parse(File.ReadAllText(path));
            var steamApps = config?.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            foreach (var (appId, app) in steamApps?.Blocks ?? [])
            {
                var minutes = long.TryParse(app.Value("Playtime"), out var m) ? m : 0;
                var last = long.TryParse(app.Value("LastPlayed"), out var l) && l > 0 ? DateTimeOffset.FromUnixTimeSeconds(l) : (DateTimeOffset?)null;
                if (minutes > 0 || last is not null)
                {
                    apps[appId] = new PlayStats(TimeSpan.FromMinutes(minutes), last);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            Program.Log($"Could not read Steam's play times: {ex.Message}");
        }

        lock (SteamLock)
        {
            _steam = (path, written, apps);
        }

        return apps;
    }

    /// <summary>
    /// The localconfig.vdf of the Steam account in use: the one signed in now, or else the one changed
    /// most recently, which is the last account that was.
    /// </summary>
    private static string? SteamConfigPath()
    {
        if (SteamLibrary.SteamDirectory() is not { } steamDir)
        {
            return null;
        }

        var userData = Path.Combine(steamDir, "userdata");
        using (var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
        {
            if (active?.GetValue("ActiveUser") is int user && user != 0)
            {
                var path = Path.Combine(userData, ((uint)user).ToString(), "config", "localconfig.vdf");
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        if (!Directory.Exists(userData))
        {
            return null;
        }

        return Directory.EnumerateDirectories(userData)
            .Select(dir => Path.Combine(dir, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
