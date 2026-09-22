using System.IO;

namespace HandheldOptimiser.Services;

public sealed class CleanTally
{
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public int Skipped { get; set; }
    public bool Blocked { get; set; }

    public void Add(CleanTally other)
    {
        FilesDeleted += other.FilesDeleted;
        BytesFreed += other.BytesFreed;
        Skipped += other.Skipped;
        Blocked |= other.Blocked;
    }

    public string Describe() =>
        $"{FilesDeleted} file(s), {BytesFreed / (1024.0 * 1024.0):0.0} MB freed" +
        (Skipped > 0 ? $", {Skipped} in use or locked and skipped" : string.Empty);
}

/// <summary>
/// Deletes cache and crash-dump files, and nothing else. Every path is checked against
/// <see cref="SafetyGuard.IsDeletionAllowed"/>, and junctions/symlinks are never followed, so a link
/// planted inside a cache folder cannot redirect the cleanup somewhere it should not go.
/// </summary>
public static class FileCleaner
{
    /// <summary>Empties a folder but keeps the folder itself, since drivers expect the cache roots to exist.</summary>
    public static CleanTally CleanDirectoryContents(string root, LogService log, CancellationToken ct)
    {
        var tally = new CleanTally();

        if (!SafetyGuard.IsDeletionAllowed(root, out var reason))
        {
            log.Error($"BLOCKED cleanup of {root}: {reason}");
            tally.Blocked = true;
            return tally;
        }

        if (!Directory.Exists(root))
        {
            log.Trace($"    {root}: not present, skipping");
            return tally;
        }

        CleanDirectory(root, log, tally, ct);
        log.Trace($"    {root}: {tally.Describe()}");
        return tally;
    }

    public static CleanTally DeleteSingleFile(string path, LogService log)
    {
        var tally = new CleanTally();

        if (!SafetyGuard.IsDeletionAllowed(path, out var reason))
        {
            log.Error($"BLOCKED deletion of {path}: {reason}");
            tally.Blocked = true;
            return tally;
        }

        if (!File.Exists(path))
        {
            log.Trace($"    {path}: not present, skipping");
            return tally;
        }

        TryDeleteFile(path, tally);
        log.Trace($"    {path}: {tally.Describe()}");
        return tally;
    }

    private static void CleanDirectory(string dir, LogService log, CleanTally tally, CancellationToken ct)
    {
        IEnumerable<string> files;
        IEnumerable<string> subdirectories;

        try
        {
            files = Directory.EnumerateFiles(dir).ToList();
            subdirectories = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"    Could not list {dir}: {ex.Message}");
            tally.Skipped++;
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Re-checked per file: cheap, and it keeps holding if the enumeration ever changes.
            if (!SafetyGuard.IsDeletionAllowed(file, out _))
            {
                tally.Skipped++;
                continue;
            }

            TryDeleteFile(file, tally);
        }

        foreach (var sub in subdirectories)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint))
                {
                    log.Trace($"    Skipping link {sub} (links are never followed)");
                    tally.Skipped++;
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tally.Skipped++;
                continue;
            }

            CleanDirectory(sub, log, tally, ct);

            try
            {
                // Non-recursive: only succeeds once everything inside has gone.
                Directory.Delete(sub, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still holds a locked file; left in place.
            }
        }
    }

    private static void TryDeleteFile(string path, CleanTally tally)
    {
        try
        {
            var info = new FileInfo(path);
            var length = info.Length;

            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }

            info.Delete();
            tally.FilesDeleted++;
            tally.BytesFreed += length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by a running game or the driver. Expected; counted, not logged per file.
            tally.Skipped++;
        }
    }
}
