using System.IO;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What a clean-up freed, and what it had to leave.</summary>
internal readonly record struct CleanResult(int Files, long Bytes, int Skipped)
{
    public string Describe() =>
        Files == 0 ? "Nothing to clear"
        : $"{Size(Bytes)} cleared" + (Skipped > 0 ? $". {Skipped} files in use were left" : string.Empty);

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : bytes > 0 ? $"{Math.Max(1, bytes >> 10)} KB"
        : "0 MB";
}

/// <summary>
/// Clean-ups a standard user may run, on this user's own folders only: temporary files, the graphics
/// drivers' shader caches and the Recycle Bin. The system-wide clean-ups stay in the main app, which runs
/// as administrator. Links inside the folders are never followed, so nothing outside them is touched.
/// </summary>
internal static partial class Cleanup
{
    // Installers and running programs keep files in the temp folder while they work.
    private static readonly TimeSpan TempMinimumAge = TimeSpan.FromDays(1);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>This user's temp folder.</summary>
    public static IReadOnlyList<string> TempFolders => [Path.GetTempPath()];

    /// <summary>
    /// The DirectX, AMD, NVIDIA and Intel shader caches. Drivers build them again as games run, the same
    /// folders the main app's Clear shader caches empties.
    /// </summary>
    public static IReadOnlyList<string> ShaderFolders =>
    [
        Path.Combine(Local, "D3DSCache"),
        Path.Combine(Local, "AMD", "DxCache"),
        Path.Combine(Local, "AMD", "DxcCache"),
        Path.Combine(Local, "AMD", "VkCache"),
        Path.Combine(Local, "AMD", "GLCache"),
        Path.Combine(Local, "NVIDIA", "DXCache"),
        Path.Combine(Local, "NVIDIA", "GLCache"),
        Path.Combine(Local, "Intel", "ShaderCache")
    ];

    /// <summary>The space the files in the folders take, for the menu rows.</summary>
    public static long SizeOf(IEnumerable<string> folders, TimeSpan? olderThan = null)
    {
        long total = 0;
        foreach (var folder in folders)
        {
            foreach (var file in Files(folder, olderThan))
            {
                total += file.Length;
            }
        }

        return total;
    }

    /// <summary>Deletes the files in the folders, keeping the folders themselves.</summary>
    public static CleanResult Clear(IEnumerable<string> folders, TimeSpan? olderThan = null)
    {
        var files = 0;
        var skipped = 0;
        long bytes = 0;

        foreach (var folder in folders)
        {
            foreach (var file in Files(folder, olderThan).ToList())
            {
                try
                {
                    var length = file.Length;
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }

                    file.Delete();
                    files++;
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by a running game or program. Expected, and left for next time.
                    skipped++;
                }
            }

            RemoveEmptyFolders(folder, olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue);
        }

        return new CleanResult(files, bytes, skipped);
    }

    public static CleanResult ClearTemp() => Clear(TempFolders, TempMinimumAge);

    public static long TempSize() => SizeOf(TempFolders, TempMinimumAge);

    /// <summary>The Recycle Bin's size on every drive, or null when Windows will not say.</summary>
    public static long? RecycleBinSize()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.Bytes : null;
    }

    /// <returns>Whether Windows emptied it.</returns>
    public static bool EmptyRecycleBin()
    {
        // No confirmation, progress or sound: the menu has already asked.
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        var result = SHEmptyRecycleBin(0, null, NoConfirmation | NoProgressUi | NoSound);

        // An already empty bin reports E_UNEXPECTED on some builds.
        return result is 0 or unchecked((int)0x8000FFFF);
    }

    /// <summary>Every file below the folder, without going into links.</summary>
    private static IEnumerable<FileInfo> Files(string folder, TimeSpan? olderThan)
    {
        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(folder);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                yield break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        var cutoff = olderThan is { } age ? DateTime.UtcNow - age : DateTime.MaxValue;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        IEnumerable<FileInfo> files;
        try
        {
            files = root.EnumerateFiles("*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            FileInfo file;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                file = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            if (file.LastWriteTimeUtc < cutoff)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Takes away the folders the clean-up emptied, deepest first, keeping the root. A folder changed since
    /// the cutoff may be one an installer has just made, so it stays.
    /// </summary>
    private static void RemoveEmptyFolders(string folder, DateTime cutoff)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var directory in Directory.EnumerateDirectories(folder, "*", options).OrderByDescending(d => d.Length).ToList())
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff)
                    {
                        continue;
                    }

                    // Not recursive: only goes once it is empty.
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still holds something.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Left as it is.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, ref RecycleBinInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint window, string? rootPath, uint flags);
}
