using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One local save backup zip kept by Save Guard.</summary>
internal sealed record SaveSnapshot(
    string ZipPath,
    string SourceFolder,
    DateTimeOffset CreatedAt,
    long SizeBytes)
{
    public string Describe(DateTimeOffset now) =>
        $"{PlayStats.Ago(now - CreatedAt)} ({CleanResult.Size(SizeBytes)})";
}

/// <summary>
/// Save Guard: keeps up to three rotating zip snapshots of a game's local save folder before it starts and
/// when it closes, so a save corrupted by a crash or an interrupted cloud sync can be restored from the
/// game's quick actions menu.
/// </summary>
internal static class SaveGuard
{
    public const int MaxSnapshots = 3;

    /// <summary>The save as it was just before the last restore. Not a save-*.zip, so never rotated away.</summary>
    public const string BeforeRestoreName = "before-restore.zip";

    // Save folders over 150 MB are skipped so a misidentified folder never stalls launch or fills the drive.
    private const long MaxSaveFolderBytes = 150L * 1024 * 1024;

    private const string SaveFoldersKey = Program.SettingsKey + @"\SaveFolders";

    private static string DefaultBackupsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandheldOptimiser", "saves");

    /// <summary>Finds the save folder for a game: its custom folder if set, Steam's remote userdata, or common save paths.</summary>
    public static string? FindSaveFolder(Game game)
    {
        if (CustomSaveFolder(game) is { } custom && Directory.Exists(custom))
        {
            return custom;
        }

        if (game.Key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) && !SteamShortcuts.IsShortcut(game))
        {
            var appId = game.Key["steam:".Length..];
            foreach (var userDir in SteamLibrary.UserDataDirectories())
            {
                try
                {
                    var remote = Path.Combine(userDir, appId, "remote");
                    if (Directory.Exists(remote) && Directory.EnumerateFileSystemEntries(remote).Any())
                    {
                        return remote;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var cleanTitle = SanitiseFolderName(game.Title);
        if (cleanTitle.Length == 0)
        {
            return null;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var candidate in new[]
        {
            Path.Combine(userProfile, "Saved Games", cleanTitle),
            Path.Combine(docs, "My Games", cleanTitle),
            Path.Combine(localApp, cleanTitle, "Saved", "SaveGames"),
            Path.Combine(localApp, cleanTitle.Replace(" ", ""), "Saved", "SaveGames"),
            Path.Combine(roamingApp, cleanTitle)
        })
        {
            try
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public static string? CustomSaveFolder(Game game)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SaveFoldersKey);
            return key?.GetValue(game.Key) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static bool SaveCustomFolder(Game game, string? folder)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SaveFoldersKey);
            if (string.IsNullOrWhiteSpace(folder))
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, folder.Trim(), RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save custom save folder for {game.Key}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Lists the snapshots stored for a game, newest first.</summary>
    public static List<SaveSnapshot> SnapshotsFor(Game game, string? backupsRoot = null)
    {
        var dir = GameBackupDir(game.Key, backupsRoot ?? DefaultBackupsRoot);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            var sourceFile = Path.Combine(dir, "source.txt");
            var recordedSource = File.Exists(sourceFile) ? File.ReadAllText(sourceFile).Trim() : string.Empty;

            return new DirectoryInfo(dir)
                .GetFiles("save-*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new SaveSnapshot(
                    f.FullName,
                    recordedSource,
                    new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                    f.Length))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Computes a stable fingerprint from save file paths, lengths, and UTC write timestamps.</summary>
    internal static string ComputeFingerprint(string folder, string[] files)
    {
        var entries = files
            .Select(f =>
            {
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
                return $"{rel}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            })
            .OrderBy(static e => e, StringComparer.OrdinalIgnoreCase);

        var joined = string.Join('\n', entries);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Creates a new zip snapshot of the game's save folder and rotates older snapshots so at most
    /// <see cref="MaxSnapshots"/> are kept. When <paramref name="skipIfUnchanged"/> is true and the
    /// folder matches the previous snapshot's fingerprint, returns the latest snapshot without re-zipping.
    /// </summary>
    public static SaveSnapshot? CreateSnapshot(Game game, string? sourceFolder = null, string? backupsRoot = null,
        bool skipIfUnchanged = false)
    {
        var folder = sourceFolder ?? FindSaveFolder(game);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                return null;
            }

            var totalBytes = files.Sum(f => new FileInfo(f).Length);
            if (totalBytes <= 0 || totalBytes > MaxSaveFolderBytes)
            {
                return null;
            }

            var root = backupsRoot ?? DefaultBackupsRoot;
            var targetDir = GameBackupDir(game.Key, root);
            var fingerprintFile = Path.Combine(targetDir, "last-fingerprint.txt");
            var fingerprint = ComputeFingerprint(folder, files);

            if (skipIfUnchanged && File.Exists(fingerprintFile)
                && string.Equals(File.ReadAllText(fingerprintFile).Trim(), fingerprint, StringComparison.Ordinal))
            {
                var existing = SnapshotsFor(game, root);
                if (existing.Count > 0)
                {
                    return existing[0];
                }
            }

            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "source.txt"), folder);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            var zipPath = Path.Combine(targetDir, $"save-{stamp}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            File.WriteAllText(fingerprintFile, fingerprint);
            RotateSnapshots(targetDir);

            var info = new FileInfo(zipPath);
            return new SaveSnapshot(zipPath, folder, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Program.Log($"Save Guard could not back up {game.Key}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The copy of the save taken just before the last restore, or null when there is none.</summary>
    public static SaveSnapshot? BeforeRestoreFor(Game game, string? backupsRoot = null)
    {
        var dir = GameBackupDir(game.Key, backupsRoot ?? DefaultBackupsRoot);
        var path = Path.Combine(dir, BeforeRestoreName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var sourceFile = Path.Combine(dir, "source.txt");
            var recordedSource = File.Exists(sourceFile) ? File.ReadAllText(sourceFile).Trim() : string.Empty;
            return new SaveSnapshot(path, recordedSource, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a snapshot back in place of the save folder (its original one, or <paramref name="targetFolder"/>).
    /// The snapshot is unpacked first, so a broken zip changes nothing. The save as it is now is kept as
    /// <see cref="BeforeRestoreName"/> beside the snapshots, and the folder is then emptied, so a file the
    /// game wrote after the snapshot cannot be left mixed in with the older ones.
    /// </summary>
    public static bool RestoreSnapshot(SaveSnapshot snapshot, string? targetFolder = null)
    {
        var destination = !string.IsNullOrWhiteSpace(targetFolder) ? targetFolder : snapshot.SourceFolder;
        var backupDir = Path.GetDirectoryName(snapshot.ZipPath);
        if (string.IsNullOrWhiteSpace(destination) || backupDir is null || !File.Exists(snapshot.ZipPath))
        {
            return false;
        }

        var unpacked = Path.Combine(Path.GetTempPath(), $"HandheldOptimiser-restore-{Guid.NewGuid():N}");
        var hadDestination = false;
        var beforeRestorePath = Path.Combine(backupDir, BeforeRestoreName);
        try
        {
            ZipFile.ExtractToDirectory(snapshot.ZipPath, unpacked);

            // The current save is kept before anything is removed. Written beside and then moved over, so the
            // last copy is not lost if this one fails, and so undoing a restore can unpack this same file.
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                var keeping = Path.Combine(backupDir, BeforeRestoreName + ".new");
                File.Delete(keeping);
                ZipFile.CreateFromDirectory(destination, keeping, CompressionLevel.Fastest, includeBaseDirectory: false);
                File.Move(keeping, beforeRestorePath, overwrite: true);
                hadDestination = true;

                foreach (var file in Directory.GetFiles(destination))
                {
                    File.Delete(file);
                }

                foreach (var folder in Directory.GetDirectories(destination))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }

            Directory.CreateDirectory(destination);
            CopyFolder(unpacked, destination);
            Program.Log($"Restored save snapshot {Path.GetFileName(snapshot.ZipPath)} to {destination}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Program.Log($"Could not restore save snapshot {snapshot.ZipPath}: {ex.Message}");
            if (hadDestination && File.Exists(beforeRestorePath))
            {
                try
                {
                    ZipFile.ExtractToDirectory(beforeRestorePath, destination, overwriteFiles: true);
                }
                catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Program.Log($"Could not roll back partial save restore: {rollbackEx.Message}");
                }
            }

            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(unpacked))
                {
                    Directory.Delete(unpacked, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log($"Could not remove {unpacked}: {ex.Message}");
            }
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var folder in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static void RotateSnapshots(string targetDir)
    {
        var zips = new DirectoryInfo(targetDir)
            .GetFiles("save-*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var old in zips.Skip(MaxSnapshots))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string GameBackupDir(string gameKey, string root)
    {
        var safe = string.Concat(gameKey.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(root, safe);
    }

    private static string SanitiseFolderName(string title) =>
        string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
}
