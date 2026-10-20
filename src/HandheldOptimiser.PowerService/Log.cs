using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// What TDP Engine did, in ProgramData\HandheldOptimiser\logs\auto-tdp.log, where every change of power
/// limit is written with the frame rate and temperature that led to it.
///
/// The service writes as SYSTEM into ProgramData, where any user may create folders. So before the first
/// line it makes sure the folders are real folders, not links, owned by Administrators or SYSTEM, with
/// access only those two can write (the installer sets the same). When they are not, it logs nothing
/// rather than write somewhere a user picked.
/// </summary>
internal static partial class Log
{
    private const long MaxSize = 512 * 1024;

    private static readonly object Gate = new();

    private static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HandheldOptimiser");

    private static readonly string Folder = Path.Combine(AppFolder, "logs");

    private static readonly string FilePath = Path.Combine(Folder, "auto-tdp.log");

    // Whether the folders passed their checks; null until the first line.
    private static bool? _safe;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                _safe ??= PrepareFolder();
                if (_safe != true)
                {
                    return;
                }

                using var file = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (!IsPlainFile(file.SafeFileHandle))
                {
                    _safe = false;
                    return;
                }

                // A change a few seconds over hours of play adds up; cap the file rather than rotate it. It
                // is emptied through the handle just checked, not deleted and made again by name.
                if (file.Length > MaxSize)
                {
                    file.SetLength(0);
                }

                file.Seek(0, SeekOrigin.End);
                file.Write(Encoding.UTF8.GetBytes($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"));
            }
            catch (Exception)
            {
                // Logging must never stop TDP Engine, whatever went wrong.
            }
        }
    }

    /// <summary>Makes or checks both folders, and clears away a log file that is not a plain one.</summary>
    private static bool PrepareFolder()
    {
        if (!SecureFolder(AppFolder) || !SecureFolder(Folder))
        {
            return false;
        }

        // From before the folder was locked, a user could have left a link in the log's place. With the
        // folder now ours, removing that name is safe: it removes the link, never what it points at.
        if (File.Exists(FilePath) || Directory.Exists(FilePath))
        {
            var attributes = File.GetAttributes(FilePath);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint) || !HasOneName(FilePath))
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(FilePath);
                }
                else
                {
                    File.Delete(FilePath);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Creates the folder with access only Administrators and SYSTEM can write. One already there must be a
    /// real folder owned by one of them, and gets that access again, as one made by an earlier version
    /// took ProgramData's, which lets users add files.
    /// </summary>
    private static bool SecureFolder(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            if (File.Exists(path))
            {
                return false;
            }

            var created = Access();
            created.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            created.CreateDirectory(path);
            info.Refresh();
        }

        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null ||
            !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
        {
            return false;
        }

        info.SetAccessControl(Access());
        return true;
    }

    /// <summary>Full control for Administrators and SYSTEM, reading for users, and nothing inherited from ProgramData.</summary>
    private static DirectorySecurity Access()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, All, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool HasOneName(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return IsPlainFile(handle);
    }

    /// <summary>Whether the open file is an ordinary one with a single name, not a link to some other file.</summary>
    private static bool IsPlainFile(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) &&
        info.NumberOfLinks == 1 &&
        (info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;

    // BY_HANDLE_FILE_INFORMATION. Its times are pairs of 32-bit numbers, so nothing is padded to 8 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
