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
    public static unsafe string? Launch(string exePath, string arguments, out int processId, bool uiAccess = false)
    {
        processId = 0;

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
                var startup = new StartupInfo { Size = sizeof(StartupInfo), Desktop = desktop };
                if (!CreateProcessAsUser(primaryToken, exePath, command, 0, 0, inheritHandles: false,
                        CreateUnicodeEnvironment, environment, directory, ref startup, out process))
                {
                    return $"The program could not be started (error {Marshal.GetLastPInvokeError()}).";
                }
            }

            CloseHandle(process.Thread);
            CloseHandle(process.Process);
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
        nint environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
