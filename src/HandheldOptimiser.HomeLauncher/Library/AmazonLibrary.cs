using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Reads games installed through Amazon Games (Prime Gaming) from its local SQLite database
/// (<c>GameInstallInfo.sqlite</c>).
/// </summary>
internal static class AmazonLibrary
{
    private static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite");

    public static IEnumerable<Game> Scan() =>
        ReadDatabase(DefaultDatabasePath, checkFolderExists: true);

    internal static List<Game> ReadDatabase(string dbPath, bool checkFolderExists = true)
    {
        if (!File.Exists(dbPath))
        {
            return [];
        }

        var rows = Sqlite.Query(
            dbPath,
            "SELECT Id, ProductTitle, InstallDirectory FROM DbSet WHERE Installed = 1;",
            3);

        if (rows is null)
        {
            return [];
        }

        var games = new List<Game>();
        foreach (var row in rows)
        {
            var id = row[0];
            var title = row[1];
            var folder = row[2];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            if (checkFolderExists && !Directory.Exists(folder))
            {
                continue;
            }

            var exe = checkFolderExists ? PrimaryExecutable(folder) : null;
            games.Add(new Game(
                Key: $"amazon:{id}",
                Title: title.Trim(),
                Store: GameStore.Other,
                CoverPath: null,
                IconPath: exe,
                LaunchTarget: $"amazon-games://play/{id}",
                LaunchArguments: null,
                WorkingDirectory: folder,
                InstallDirectory: folder,
                ExecutablePath: exe));
        }

        return games;
    }

    private static string? PrimaryExecutable(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
