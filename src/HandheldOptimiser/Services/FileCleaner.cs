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
        Plural.Count(FilesDeleted, "file") + $", {BytesFreed / (1024.0 * 1024.0):0.0} MB freed" +
        (Skipped > 0 ? $", {Skipped} in use or locked and skipped" : string.Empty);
}

/// <summary>
/// Deletes cache and crash-dump files, and nothing else. Every path is checked against
/// <see cref="SafetyGuard.IsDeletionAllowed"/>, and junctions/symlinks are never followed, so a link
/// planted in or above a cache folder cannot redirect the cleanup somewhere it should not go.
///
/// The folders above matter as much as the ones below. Several cleanup roots are under %LOCALAPPDATA%,
/// which any unelevated process running as the user can write to, and this app runs elevated. Swapping
/// %LOCALAPPDATA%\CrashDumps for a junction to a system folder would otherwise turn a cache clean
/// into an administrator deleting that folder's contents.
///
/// The path checks only narrow that window, since a folder can be swapped after it was checked. So
/// every delete goes through <see cref="SafeDelete"/>, which opens the file without following links,
/// reads back where the handle really is, and deletes through that handle only if it is still inside
/// the folder being cleaned.
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

        if (SafetyGuard.FindLinkOnPath(root) is { } link)
        {
            log.Error($"BLOCKED cleanup of {root}: {link} is a junction or symbolic link, which is never followed.");
            tally.Blocked = true;
            return tally;
        }

        var fullRoot = Path.GetFullPath(root);

        if (!SafeDelete.IsVerifiedDirectory(fullRoot, fullRoot, out var problem))
        {
            log.Error($"BLOCKED cleanup of {root}: {problem}");
            tally.Blocked = true;
            return tally;
        }

        CleanDirectory(fullRoot, fullRoot, log, tally, ct);
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

        if (SafetyGuard.FindLinkOnPath(path) is { } link)
        {
            log.Error($"BLOCKED deletion of {path}: {link} is a junction or symbolic link, which is never followed.");
            tally.Blocked = true;
            return tally;
        }

        // The file is its own root: its handle has to lead to exactly this path.
        var full = Path.GetFullPath(path);
        TryDeleteFile(full, full, log, tally);
        log.Trace($"    {path}: {tally.Describe()}");
        return tally;
    }

    /// <param name="root">The folder being cleaned. Nothing outside it is deleted, whatever a path leads to.</param>
    private static void CleanDirectory(string root, string dir, LogService log, CleanTally tally, CancellationToken ct)
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

            // A first filter by path. The check that holds is made on the handle, in SafeDelete.
            if (!SafetyGuard.IsDeletionAllowed(file, out _))
            {
                tally.Skipped++;
                continue;
            }

            TryDeleteFile(file, root, log, tally);
        }

        foreach (var sub in subdirectories)
        {
            ct.ThrowIfCancellationRequested();

            if (!SafetyGuard.IsDeletionAllowed(sub, out _))
            {
                tally.Skipped++;
                continue;
            }

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

            // Checked by handle too, so a folder swapped for a junction since it was listed is not
            // walked. Every delete inside would be refused anyway; this saves going through them all.
            if (!SafeDelete.IsVerifiedDirectory(sub, root, out var problem))
            {
                log.Warning($"    Skipping {sub}: {problem}");
                tally.Skipped++;
                continue;
            }

            CleanDirectory(root, sub, log, tally, ct);

            // Only succeeds once everything inside has gone. Not empty means it still holds a locked
            // file, and it is left in place.
            if (SafeDelete.DeleteEmptyDirectory(sub, root, out problem) == SafeDeleteOutcome.Refused)
            {
                log.Error($"BLOCKED removal of {sub}: {problem}");
            }
        }
    }

    private static void TryDeleteFile(string path, string root, LogService log, CleanTally tally)
    {
        switch (SafeDelete.DeleteFile(path, root, out var length, out var problem))
        {
            case SafeDeleteOutcome.Deleted:
                tally.FilesDeleted++;
                tally.BytesFreed += length;
                break;

            case SafeDeleteOutcome.Missing:
                // Gone since it was listed, which is what was wanted.
                break;

            case SafeDeleteOutcome.Refused:
                log.Error($"BLOCKED deletion of {path}: {problem}");
                tally.Skipped++;
                break;

            default:
                // In use by a running game or the driver. Expected; counted, not logged per file.
                tally.Skipped++;
                break;
        }
    }
}
