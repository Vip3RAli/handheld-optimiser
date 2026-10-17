using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.Services;

public enum SafeDeleteOutcome
{
    Deleted,

    /// <summary>Already gone. Nothing to count.</summary>
    Missing,

    /// <summary>Open in something else without sharing delete access, as a running game holds its cache.</summary>
    InUse,

    /// <summary>A folder that still has something in it.</summary>
    NotEmpty,

    /// <summary>Not deleted because it is not what it should be: a link, or somewhere outside the folder being cleaned.</summary>
    Refused,

    Failed
}

/// <summary>
/// Deletes a file or an empty folder through a handle, after checking where that handle really leads.
///
/// Checking a path for junctions and then deleting by the same path leaves a gap: something running as
/// the user can swap a folder it owns (anything under %LOCALAPPDATA%) for a junction in between, and
/// the delete, which runs as administrator, then lands wherever the junction points. Here the file is
/// opened once, without following any link, its real location is read back from the handle, and the
/// delete goes through that same handle. Whatever the path leads to by then, what is deleted is what
/// was checked. The same pattern as <see cref="SessionFile"/>, which writes the log this way.
/// </summary>
public static class SafeDelete
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x0080;

    // No FILE_SHARE_DELETE: while the handle is open nothing else can rename or delete the file, so it
    // cannot be moved somewhere else between the check and the delete.
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;

    private const uint AttributeReadOnly = 0x0001;
    private const uint AttributeDirectory = 0x0010;
    private const uint AttributeReparsePoint = 0x0400;

    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    private const int ErrorInvalidFunction = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorDirNotEmpty = 145;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle file, int infoClass, ref byte deleteFile, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDispositionEx(SafeFileHandle file, int infoClass, ref uint flags, uint size);

    /// <summary>
    /// Deletes one file, provided its handle leads inside <paramref name="allowedRoot"/> (or is that
    /// path itself, for a single file such as MEMORY.DMP). <paramref name="bytes"/> is its size when it
    /// was deleted; <paramref name="problem"/> says why one was refused or failed.
    /// </summary>
    public static SafeDeleteOutcome DeleteFile(string path, string allowedRoot, out long bytes, out string? problem) =>
        DeleteByHandle(path, allowedRoot, directory: false, out bytes, out problem);

    /// <summary>
    /// Removes a folder below <paramref name="allowedRoot"/> if it is empty. Never the root itself:
    /// the drivers expect their cache folders to be there.
    /// </summary>
    public static SafeDeleteOutcome DeleteEmptyDirectory(string path, string allowedRoot, out string? problem) =>
        DeleteByHandle(path, allowedRoot, directory: true, out _, out problem);

    /// <summary>
    /// Whether <paramref name="path"/> is a real folder (not a link) whose handle leads inside
    /// <paramref name="allowedRoot"/>. Checked before going into a folder, so one swapped for a junction
    /// to somewhere large is not walked file by file only to have every delete refused.
    /// </summary>
    public static bool IsVerifiedDirectory(string path, string allowedRoot, out string? problem)
    {
        using var handle = CreateFileW(
            path, FileReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
            FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        return Verify(handle, path, allowedRoot, directory: true, allowRootItself: true, out _, out problem);
    }

    private static SafeDeleteOutcome DeleteByHandle(
        string path, string allowedRoot, bool directory, out long bytes, out string? problem)
    {
        bytes = 0;

        using var handle = CreateFileW(
            path, DeleteAccess | FileReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
            FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            problem = new Win32Exception(error).Message;

            return error switch
            {
                ErrorFileNotFound or ErrorPathNotFound => SafeDeleteOutcome.Missing,
                ErrorSharingViolation or ErrorLockViolation => SafeDeleteOutcome.InUse,
                _ => SafeDeleteOutcome.Failed
            };
        }

        // The root folder of a clean is only ever emptied, never removed.
        if (!Verify(handle, path, allowedRoot, directory, allowRootItself: !directory, out var size, out problem))
        {
            return SafeDeleteOutcome.Refused;
        }

        var deleteError = MarkForDeletion(handle);
        if (deleteError == 0)
        {
            bytes = size;
            problem = null;
            return SafeDeleteOutcome.Deleted;
        }

        problem = new Win32Exception(deleteError).Message;

        return deleteError switch
        {
            ErrorDirNotEmpty => SafeDeleteOutcome.NotEmpty,
            ErrorSharingViolation or ErrorLockViolation or ErrorAccessDenied => SafeDeleteOutcome.InUse,
            _ => SafeDeleteOutcome.Failed
        };
    }

    /// <summary>
    /// The checks made on the open handle: it is the kind of thing expected, it is not itself a link,
    /// it has no other names, and it really is inside the folder being cleaned and inside the folders
    /// <see cref="SafetyGuard.IsDeletionAllowed"/> lets this app delete from.
    /// </summary>
    private static bool Verify(
        SafeFileHandle handle, string path, string allowedRoot, bool directory, bool allowRootItself,
        out long size, out string? problem)
    {
        size = 0;

        if (!GetFileInformationByHandle(handle, out var info))
        {
            problem = $"its attributes could not be read ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).";
            return false;
        }

        if ((info.FileAttributes & AttributeReparsePoint) != 0)
        {
            problem = "it is a junction or symbolic link, which is never followed.";
            return false;
        }

        var isDirectory = (info.FileAttributes & AttributeDirectory) != 0;
        if (isDirectory != directory)
        {
            problem = directory ? "it is a file, not a folder." : "it is a folder, not a file.";
            return false;
        }

        // A second name for the file is not something a cache makes. Deleting it would free nothing,
        // and the other name may be somewhere this app has no business with.
        if (!directory && info.NumberOfLinks > 1)
        {
            problem = "it has other names (hard links) elsewhere, so it is left alone.";
            return false;
        }

        var actual = FinalPath(handle);
        if (actual is null)
        {
            problem = "where it really is could not be read.";
            return false;
        }

        if (!IsWithinRoot(Path.GetFullPath(allowedRoot), actual, allowRootItself) ||
            !SafetyGuard.IsDeletionAllowed(NormaliseFinalPath(actual), out _))
        {
            problem = $"{path} now leads to {NormaliseFinalPath(actual)}, outside the folder being cleaned.";
            return false;
        }

        size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        problem = null;
        return true;
    }

    /// <summary>Deletes what the handle refers to. Returns 0, or the Windows error code.</summary>
    private static int MarkForDeletion(SafeFileHandle handle)
    {
        // POSIX semantics take the name away at once, even if something else has the file open with
        // delete sharing, and the read-only attribute is ignored rather than cleared first.
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        if (SetDispositionEx(handle, FileDispositionInfoExClass, ref flags, sizeof(uint)))
        {
            return 0;
        }

        var error = Marshal.GetLastWin32Error();

        // Older Windows, or a FAT32 or exFAT microSD card, does not support those flags. The classic
        // call works there, though not on a read-only file, which is then counted as skipped.
        if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction)
        {
            byte deleteFile = 1;
            if (SetDisposition(handle, FileDispositionInfoClass, ref deleteFile, 1))
            {
                return 0;
            }

            error = Marshal.GetLastWin32Error();
        }

        return error;
    }

    /// <summary>Where the handle really points, as Windows reports it, or null if it cannot be read.</summary>
    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];

        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            // Too small: the answer is the size needed. Paths are capped at 32,767 characters.
            if (length > 32768)
            {
                return null;
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>
    /// A path as Windows reports it from a handle (\\?\C:\..., \\?\UNC\server\share\...), turned into the
    /// plain form the rest of the app compares against, with no trailing backslash.
    /// </summary>
    public static string NormaliseFinalPath(string path)
    {
        var result = path.Replace('/', '\\');

        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            result = @"\\" + result[8..];
        }
        else if (result.StartsWith(@"\\?\", StringComparison.Ordinal) || result.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            result = result[4..];
        }

        return result.TrimEnd('\\');
    }

    /// <summary>
    /// Whether <paramref name="finalPath"/>, read back from a handle, is <paramref name="root"/> or
    /// somewhere below it. Compared without regard to case, as NTFS names are, and with either side in
    /// the \\?\ form. Anything that cannot be compared with confidence (empty, relative steps, a volume
    /// GUID path that names no drive) answers false: when in doubt, nothing is deleted.
    /// </summary>
    public static bool IsWithinRoot(string root, string? finalPath, bool allowRootItself = true)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(finalPath))
        {
            return false;
        }

        var normalRoot = NormaliseFinalPath(root);
        var normalPath = NormaliseFinalPath(finalPath);

        if (normalRoot.Length == 0 || normalPath.Length == 0 ||
            HasRelativeStep(normalRoot) || HasRelativeStep(normalPath))
        {
            return false;
        }

        if (normalPath.Equals(normalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return allowRootItself;
        }

        return normalPath.StartsWith(normalRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRelativeStep(string path) =>
        path.Split('\\').Any(part => part is "." or "..");
}
