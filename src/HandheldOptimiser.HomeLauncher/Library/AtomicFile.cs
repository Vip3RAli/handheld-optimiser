using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Writes a file whole or not at all: to a temporary file beside it, then moved over it. The library can
/// be closed, or the device lose power, in the middle of a write, and a half-written cache file would be
/// read back next time as a broken picture or list.
/// </summary>
internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        var temp = TempFor(path);
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(string path, string text)
    {
        var temp = TempFor(path);
        await File.WriteAllTextAsync(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    // Beside the file, so the move stays on the same drive and is a rename rather than a copy.
    private static string TempFor(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return path + ".tmp";
    }
}
