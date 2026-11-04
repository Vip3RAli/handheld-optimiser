using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Starts a program in the signed-in player's session, as that player, from the service, which runs as
/// SYSTEM in session 0 where nothing it starts could be seen.
/// </summary>
internal static partial class SessionLauncher
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int WtsSessionInfoEx = 25;

    // WTS_SESSIONSTATE_LOCK. On Windows 7 and Server 2008 R2 the lock and unlock values are swapped, but
    // only Windows 11 is supported, so the documented values are used.
    private const int SessionStateLock = 0;

    // WTSINFOEXW is a DWORD level then a union that holds WTSINFOEX_LEVEL1_W. That has LARGE_INTEGER fields,
    // so the union starts at 8, and SessionFlags is the level 1 structure's third 4 byte field.
    private const int LevelOffset = 0;
    private const int SessionFlagsOffset = 8 + 8;

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    // TOKEN_INFORMATION_CLASS TokenUIAccess.
    private const int TokenUIAccess = 26;

    /// <summary>The session of the player at the console, or null when nobody is signed in.</summary>
    public static uint? ActiveSession() => WTSGetActiveConsoleSessionId() is var session && session != NoSession ? session : null;

    /// <summary>
    /// Starts <paramref name="exePath"/> with <paramref name="arguments"/> on the signed-in player's desktop.
    /// </summary>
    /// <param name="processId">The started process's ID, or 0 when it did not start.</param>
    /// <param name="uiAccess">
    /// Sets the UIAccess flag on the token, which puts the program's topmost windows in the band above full
    /// screen games, and lets it past the walls between programs of different rights. Windows otherwise
    /// gives it only to programs signed by a trusted publisher in Program Files, and setting it takes the
    /// TCB privilege, which this service has as SYSTEM. Only for the overlay and the stats screen, which are
    /// this app's own and in its own folder. Should Windows refuse it, the program starts without it.
    /// </param>
    /// <returns>Why it did not start, or null when it did.</returns>
    public static string? Launch(string exePath, string arguments, out int processId, bool uiAccess = false) =>
        Start(exePath, arguments, uiAccess, keepHandle: false, out processId, out _);

    /// <summary>
    /// Starts <paramref name="exePath"/> on the signed-in player's desktop as <see cref="Launch"/> does, and waits
    /// up to <paramref name="timeout"/> for it to finish. For a short job that has to run as the player.
    /// </summary>
    /// <param name="exitCode">What it ended with, or null when it did not start or finish.</param>
    /// <param name="output">What it printed, or null when it did not start or finish.</param>
    /// <returns>Why there is no exit code, or null when there is.</returns>
    public static unsafe string? Run(string exePath, string arguments, TimeSpan timeout, out int? exitCode, out string? output)
    {
        exitCode = null;
        output = null;

        // The program's output goes into a pipe. Only the end it writes to is passed on to it; this end is not.
        var security = new SecurityAttributes { Length = sizeof(SecurityAttributes), InheritHandle = 1 };
        if (!CreatePipe(out var readEnd, out var writeEnd, ref security, OutputPipeSize))
        {
            return $"A pipe for its output could not be made (error {Marshal.GetLastPInvokeError()}).";
        }

        using var reader = new SafeFileHandle(readEnd, ownsHandle: true);
        nint handle;
        try
        {
            SetHandleInformation(readEnd, HandleFlagInherit, 0);
            if (Start(exePath, arguments, uiAccess: false, keepHandle: true, out _, out handle, writeEnd) is { } refused)
            {
                return refused;
            }
        }
        finally
        {
            // Closed here as well as in the program, so the pipe ends when the program does.
            CloseHandle(writeEnd);
        }

        try
        {
            if (WaitForSingleObject(handle, (uint)timeout.TotalMilliseconds) != WaitObject0)
            {
                return "It did not finish in time.";
            }

            if (!GetExitCodeProcess(handle, out var code))
            {
                return $"Its result could not be read (error {Marshal.GetLastPInvokeError()}).";
            }

            exitCode = (int)code;

            // It has ended, so the pipe holds all it printed, which is far less than the pipe's size.
            using var stream = new FileStream(reader, FileAccess.Read, 1);
            using var text = new StreamReader(stream);
            output = text.ReadToEnd();
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <param name="keepHandle">Hand back the process's handle, for the caller to wait on and close, rather than close it.</param>
    /// <param name="standardOutput">An inheritable handle the program's output goes to, the only handle it is given, or 0 for none.</param>
    private static unsafe string? Start(string exePath, string arguments, bool uiAccess, bool keepHandle, out int processId,
        out nint processHandle, nint standardOutput = 0)
    {
        processId = 0;
        processHandle = 0;

        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            return "Nobody is signed in.";
        }

        if (IsLocked(session) is { } lockedOrError)
        {
            return lockedOrError;
        }

        nint userToken = 0;
        nint primaryToken = 0;
        nint environment = 0;
        try
        {
            if (!WTSQueryUserToken(session, out userToken))
            {
                return $"The signed-in player's token could not be read (error {Marshal.GetLastPInvokeError()}).";
            }

            if (!DuplicateTokenEx(userToken, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                return $"The player's token could not be copied (error {Marshal.GetLastPInvokeError()}).";
            }

            if (uiAccess)
            {
                var on = 1;
                if (!SetTokenInformation(primaryToken, TokenUIAccess, &on, sizeof(int)))
                {
                    Log.Write($"Windows would not set UIAccess for {Path.GetFileName(exePath)} (error {Marshal.GetLastPInvokeError()}), so it starts without it");
                }
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, inherit: false))
            {
                return $"The player's environment could not be made (error {Marshal.GetLastPInvokeError()}).";
            }

            // CreateProcessAsUser may write to the command line, so it is given a buffer of its own.
            var commandLine = (arguments.Length == 0 ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}") + "\0";
            var commandBuffer = commandLine.ToCharArray();
            var directory = Path.GetDirectoryName(exePath);

            ProcessInformation process;
            fixed (char* desktop = @"winsta0\default")
            fixed (char* command = commandBuffer)
            {
                var startup = new StartupInfoEx();
                startup.StartupInfo.Size = sizeof(StartupInfo);
                startup.StartupInfo.Desktop = desktop;
                var flags = CreateUnicodeEnvironment;
                byte* attributes = null;
                var listMade = false;

                // Where the list points, so it has to outlive the call that starts the program.
                var passed = standardOutput;
                try
                {
                    if (standardOutput != 0)
                    {
                        // A list naming the one handle to pass on, so nothing else the service has open that
                        // happens to be inheritable reaches the program.
                        nuint size = 0;
                        InitializeProcThreadAttributeList(null, 1, 0, ref size);
                        attributes = (byte*)NativeMemory.Alloc(size);
                        listMade = InitializeProcThreadAttributeList(attributes, 1, 0, ref size);
                        if (!listMade
                            || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, &passed, (nuint)sizeof(nint), null, null))
                        {
                            return $"The program's output could not be set up (error {Marshal.GetLastPInvokeError()}).";
                        }

                        startup.StartupInfo.Size = sizeof(StartupInfoEx);
                        startup.StartupInfo.Flags = StartfUseStdHandles;
                        startup.StartupInfo.StdOutput = standardOutput;
                        startup.AttributeList = attributes;
                        flags |= ExtendedStartupInfoPresent;
                    }

                    if (!CreateProcessAsUser(primaryToken, exePath, command, 0, 0, inheritHandles: standardOutput != 0,
                            flags, environment, directory, &startup, out process))
                    {
                        return $"The program could not be started (error {Marshal.GetLastPInvokeError()}).";
                    }
                }
                finally
                {
                    if (listMade)
                    {
                        DeleteProcThreadAttributeList(attributes);
                    }

                    if (attributes != null)
                    {
                        NativeMemory.Free(attributes);
                    }
                }
            }

            CloseHandle(process.Thread);
            if (keepHandle)
            {
                processHandle = process.Process;
            }
            else
            {
                CloseHandle(process.Process);
            }

            processId = process.ProcessId;
            return null;
        }
        finally
        {
            if (environment != 0)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != 0)
            {
                CloseHandle(primaryToken);
            }

            if (userToken != 0)
            {
                CloseHandle(userToken);
            }
        }
    }

    /// <summary>"The screen is locked." when it is, a reason when the session cannot be read, or null.</summary>
    private static string? IsLocked(uint session)
    {
        if (!WTSQuerySessionInformation(0, session, WtsSessionInfoEx, out var buffer, out var bytes))
        {
            return $"The player's session could not be read (error {Marshal.GetLastPInvokeError()}).";
        }

        try
        {
            if (bytes < SessionFlagsOffset + 4 || Marshal.ReadInt32(buffer, LevelOffset) != 1)
            {
                return "The player's session could not be read.";
            }

            return Marshal.ReadInt32(buffer, SessionFlagsOffset) == SessionStateLock ? "The screen is locked." : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // STARTUPINFOW, with the desktop the only field set.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StartupInfo
    {
        public int Size;
        public char* Reserved;
        public char* Desktop;
        public char* Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    // STARTUPINFOEXW: the above, then the attribute list.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public byte* AttributeList;
    }

    // SECURITY_ATTRIBUTES, only to make a pipe's handle inheritable.
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    private const int StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;

    // Far more than the Radeon settings line, so the program never waits for the pipe to be read.
    private const int OutputPipeSize = 64 * 1024;

    // PROCESS_INFORMATION.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, uint session, int infoClass, out nint buffer,
        out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint session, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint token, uint access, nint attributes, int impersonationLevel,
        int tokenType, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetTokenInformation(nint token, int infoClass, void* information, int length);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUser(nint token, string applicationName, char* commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        nint environment, string? currentDirectory, StartupInfoEx* startup, out ProcessInformation process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InitializeProcThreadAttributeList(byte* list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(byte* list, uint flags, nuint attribute, void* value, nuint size,
        void* previousValue, nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial void DeleteProcThreadAttributeList(byte* list);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private const uint WaitObject0 = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);
}
