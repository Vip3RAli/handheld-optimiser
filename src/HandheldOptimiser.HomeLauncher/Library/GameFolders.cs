using System.IO;
using HandheldOptimiser.PowerService;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The folders of every installed game in the library, saved in the player's own settings for the TDP Engine
/// service. A session started from the Game Bar widget only counts programs inside one of them, so a
/// browser or anything else under Apps is never taken for the game.
/// </summary>
internal static class GameFolders
{
    /// <summary>Each installed game's folder once, leaving out Apps and games with no folder.</summary>
    public static string[] From(IEnumerable<Game> games, Func<Game, string?> folderOf) =>
        [.. games
            .Where(game => game.Installed && !game.IsApp)
            .Select(folderOf)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Saves the list for the service. Call it off the UI thread: each folder is checked on disk.</summary>
    public static void Save(IEnumerable<Game> games)
    {
        var folders = From(games, GameSession.ProcessFolder);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoTdpProtocol.LibrarySettingsKey);
            key.SetValue(AutoTdpProtocol.GameFoldersValue, folders, RegistryValueKind.MultiString);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The widget then keeps to the busiest program, as it did before the list existed.
            Program.Log($"Could not save the game folders for TDP Engine: {ex.Message}");
        }
    }
}
