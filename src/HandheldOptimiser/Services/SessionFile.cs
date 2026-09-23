using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.Services;

public enum AppendResult
{
    Written,

    /// <summary>Something has the file open without sharing write access, usually a viewer reading it.</summary>
    Busy,

    /// <summary>The file cannot be written safely any more; see the problem text.</summary>
    Lost
}

/// <summary>
/// The session log file, in a folder the user can write to, written without letting a junction or
/// symbolic link send an elevated write somewhere else.
///
/// Checking the folders for links narrows the window but cannot close it: one could be swapped in
/// between the check and the open. So every time the file is opened, its real location is read back
/// from the handle, and nothing is written unless it is the intended file. A new file that turns out to
/// be in the wrong place is deleted through that same handle, which needs no second path lookup.
///
/// The file is reopened for each write rather than held open. Notepad, among others, refuses to open a
/// file that another process holds open for writing, and the log is no use if it cannot be read while
/// the app is running.
/// </summary>
internal sealed class SessionFile
{
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const int ErrorFileExists = 80;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int FileDispositionInfoClass = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte deleteFile, uint size);

    private static readonly UTF8Encoding Utf8 = new(false);

    public string Path { get; }

    private SessionFile(string path) => Path = path;

    /// <summary>
    /// Creates a new, empty log file named after <paramref name="stamp"/> in <paramref name="dir"/>.
    /// Returns null, with the reason in <paramref name="problem"/>, if it could not be created safely.
    /// </summary>
    public static SessionFile? Create(string dir, string stamp, out string? problem)
    {
        try
        {
            if (SafetyGuard.FindLinkOnPath(dir) is { } before)
            {
                problem = $"{before} is a junction or symbolic link, which is never written through.";
                return null;
            }

            Directory.CreateDirectory(dir);

            if (SafetyGuard.FindLinkOnPath(dir) is { } after)
            {
                problem = $"{after} is a junction or symbolic link, which is never written through.";
                return null;
            }

            // Two sessions started in the same second, or a name planted ahead of time: never reuse a file.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var candidate = System.IO.Path.Combine(dir, attempt == 1 ? $"{stamp}.log" : $"{stamp}-{attempt}.log");

                using var handle = CreateFileW(candidate, GenericWrite | Delete, ShareAll, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();

                    if (error == ErrorFileExists)
                    {
                        continue;
                    }

                    problem = new Win32Exception(error).Message;
                    return null;
                }

                var actual = FinalPath(handle);
                if (!IsSamePath(actual, candidate))
                {
                    byte deleteFile = 1;
                    SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, 1);
                    problem = $"the log file was redirected to {actual ?? "an unknown location"}, so it was removed.";
                    return null;
                }

                problem = null;
                return new SessionFile(candidate);
            }

            problem = "every log file name for this second was already taken.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Appends <paramref name="text"/>, opening the existing file only; nothing is ever created here, so
    /// a redirected open cannot leave a file behind. <paramref name="problem"/> is set for
    /// <see cref="AppendResult.Lost"/>.
    /// </summary>
    public AppendResult TryAppend(string text, out string? problem)
    {
        problem = null;

        using var handle = CreateFileW(Path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();

            if (error is ErrorSharingViolation or ErrorLockViolation)
            {
                return AppendResult.Busy;
            }

            problem = new Win32Exception(error).Message;
            return AppendResult.Lost;
        }

        var actual = FinalPath(handle);
        if (!IsSamePath(actual, Path))
        {
            // An existing file somewhere else, reached through a link planted since the last write.
            // Not ours, so it is left exactly as it is.
            problem = $"the log file now leads to {actual ?? "an unknown location"}, so nothing more is written to it.";
            return AppendResult.Lost;
        }

        try
        {
            using var stream = new FileStream(handle, FileAccess.Write);
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Utf8.GetBytes(text));
            return AppendResult.Written;
        }
        catch (IOException ex)
        {
            problem = ex.Message;
            return AppendResult.Lost;
        }
    }

    private static bool IsSamePath(string? actual, string expected) =>
        string.Equals(actual, System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

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
