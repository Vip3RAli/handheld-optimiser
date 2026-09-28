using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Every installed game across the supported stores, most recently launched from the library first.
/// </summary>
internal static class GameCatalog
{
    // Launch times, one value per game key, so the last game played is always the first tile.
    private const string HistoryKey = Program.SettingsKey + @"\LastLaunched";

    public static List<Game> Scan()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);

        foreach (var (store, scan) in new (string, Func<IEnumerable<Game>>)[]
        {
            ("Steam", SteamLibrary.Scan),
            ("Epic", EpicLibrary.Scan),
            ("Battle.net", BattleNetLibrary.Scan),
            ("GOG", GogLibrary.Scan)
        })
        {
            try
            {
                foreach (var game in scan())
                {
                    games.TryAdd(game.Key, game);
                }
            }
            catch (Exception ex)
            {
                // One store's odd install must not empty the whole library.
                Program.Log($"Scanning {store} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var history = ReadHistory();
        return games.Values
            .OrderByDescending(g => history.GetValueOrDefault(g.Key))
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <returns>An error message, or null when the game was started.</returns>
    public static string? Launch(Game game)
    {
        try
        {
            Process.Start(new ProcessStartInfo(game.LaunchTarget)
            {
                Arguments = game.LaunchArguments ?? string.Empty,
                WorkingDirectory = game.WorkingDirectory ?? string.Empty,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Program.Log($"Failed to launch {game.Key} ({game.LaunchTarget}): {ex.Message}");
            return $"{game.Title} could not be started: {ex.Message}";
        }

        Program.Log($"Launched {game.Key}");
        RecordLaunch(game);
        return null;
    }

    private static Dictionary<string, long> ReadHistory()
    {
        var history = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HistoryKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is long when)
                {
                    history[name] = when;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unsorted is fine.
        }

        return history;
    }

    private static void RecordLaunch(Game game)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(HistoryKey);
            key.SetValue(game.Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.QWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not record launch of {game.Key}: {ex.Message}");
        }
    }
}
