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

    // The player's extra launch arguments, one value per game key.
    private const string ArgumentsKey = Program.SettingsKey + @"\LaunchArguments";

    // Games the player starred or hid, one value per game key.
    private const string FavouritesKey = Program.SettingsKey + @"\Favourites";
    private const string HiddenKey = Program.SettingsKey + @"\Hidden";

    public static List<Game> Scan()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);

        // Install folders already taken by an earlier store. EA and Ubisoft games bought on Steam or Epic
        // register with the EA App and Ubisoft Connect as well, so those two are scanned last and a game
        // keeps the store it was bought from.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (store, scan) in new (string, Func<IEnumerable<Game>>)[]
        {
            ("Steam", SteamLibrary.Scan),
            ("Xbox", XboxLibrary.Scan),
            ("Epic", EpicLibrary.Scan),
            ("Battle.net", BattleNetLibrary.Scan),
            ("GOG", GogLibrary.Scan),
            ("EA App", EaLibrary.Scan),
            ("Ubisoft Connect", UbisoftLibrary.Scan)
        })
        {
            var folders = new List<string>();
            try
            {
                foreach (var game in scan())
                {
                    var folder = NormaliseFolder(game.InstallDirectory);
                    if (folder is not null && claimed.Contains(folder))
                    {
                        continue;
                    }

                    if (games.TryAdd(game.Key, game) && folder is not null)
                    {
                        folders.Add(folder);
                    }
                }
            }
            catch (Exception ex)
            {
                // One store's odd install must not empty the whole library.
                Program.Log($"Scanning {store} failed: {ex.GetType().Name}: {ex.Message}");
            }

            claimed.UnionWith(folders);
        }

        // Programs the player added are listed whatever folder they are in: they asked for them.
        try
        {
            foreach (var game in AddedPrograms.Scan())
            {
                games.TryAdd(game.Key, game);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Reading the added programs failed: {ex.GetType().Name}: {ex.Message}");
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
            var arguments = new[] { game.LaunchArguments, CustomArguments(game) }.Where(a => !string.IsNullOrWhiteSpace(a));

            Process.Start(new ProcessStartInfo(game.LaunchTarget)
            {
                Arguments = string.Join(' ', arguments),
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

    /// <summary>
    /// Whether the player's own arguments can reach the game. Steam passes on whatever follows
    /// -applaunch and GOG games are started from their exe; the other stores start the game themselves.
    /// </summary>
    public static bool SupportsCustomArguments(Game game) =>
        game.Store is GameStore.Steam or GameStore.Gog
        || (game.Store == GameStore.Other && game.LaunchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys of the games the player starred.</summary>
    public static HashSet<string> Favourites() => ReadMarks(FavouritesKey);

    /// <summary>The keys of the games the player hid from the grid.</summary>
    public static HashSet<string> Hidden() => ReadMarks(HiddenKey);

    /// <returns>False when the choice could not be stored.</returns>
    public static bool SetFavourite(Game game, bool favourite) => WriteMark(FavouritesKey, game, favourite);

    /// <returns>False when the choice could not be stored.</returns>
    public static bool SetHidden(Game game, bool hidden) => WriteMark(HiddenKey, game, hidden);

    private static HashSet<string> ReadMarks(string keyName)
    {
        var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyName);
            marked.UnionWith(key?.GetValueNames() ?? []);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Nothing starred or hidden is a fine fallback.
        }

        return marked;
    }

    private static bool WriteMark(string keyName, Game game, bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyName);
            if (on)
            {
                key.SetValue(game.Key, 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save {keyName} for {game.Key}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The arguments the player added for this game in the quick actions menu, if any.</summary>
    public static string? CustomArguments(Game game)
    {
        if (!SupportsCustomArguments(game))
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ArgumentsKey);
            return key?.GetValue(game.Key) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <returns>False when the arguments could not be stored.</returns>
    public static bool SaveCustomArguments(Game game, string arguments)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ArgumentsKey);
            if (arguments.Length == 0)
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, arguments, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save launch arguments for {game.Key}: {ex.Message}");
            return false;
        }
    }

    private static string? NormaliseFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
