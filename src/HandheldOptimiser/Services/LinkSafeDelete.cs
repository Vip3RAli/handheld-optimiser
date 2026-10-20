using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.Services;

/// <summary>
/// Deletes a file in a folder the user can write to without letting a junction or symbolic link turn
/// the delete, done as administrator, onto a file somewhere else.
///
/// Checking the path for links first (<see cref="SafetyGuard.FindLinkOnPath"/>) leaves a gap between
/// the check and the delete, in which a folder could be swapped for a junction. So the file is opened
/// once, without following a link in its own name, its real location is read back from that handle,
/// and it is deleted through the same handle only if it is the intended file. The same approach as
/// <see cref="SessionFile"/> uses for the log.
/// </summary>
internal static class LinkSafeDelete
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoClass = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte deleteFile, uint size);

    /// <summary>
    /// Deletes <paramref name="path"/> if it is really there. Returns false, with the reason in
    /// <paramref name="problem"/>, if it could not be opened or turned out to lead somewhere else.
    /// </summary>
    public static bool TryDelete(string path, out string? problem)
    {
        // A link in the file's own name is opened as the link, not followed, so at worst the link
        // itself is removed. A link in a folder above it is followed by the open, and caught below.
        using var handle = CreateFileW(
            path, Delete | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var actual = FinalPath(handle);
        if (!string.Equals(actual, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            problem = $"it now leads to {actual ?? "an unknown location"}, so it was left alone.";
            return false;
        }

        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, 1))
        {
            problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Where the handle really points, as a plain drive path, or null if it cannot be read.</summary>
    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var result = new string(buffer, 0, (int)length);

        return result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + result[8..]
            : result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..]
            : result;
    }
}
