using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Whether this session's screen is locked, which is read the same way as in TDP Engine's SessionLauncher.</summary>
internal static partial class SessionLock
{
    private const nint CurrentServer = 0;
    private const uint CurrentSession = 0xFFFFFFFF;
    private const int SessionInfoEx = 25;
    private const int LevelOffset = 0;

    // WTSINFOEXW: the level, then WTSINFOEX_LEVEL1_W at 8 (it holds 8 byte values), whose SessionFlags follow
    // its SessionId and SessionState.
    private const int SessionFlagsOffset = 8 + 8;
    private const int SessionStateLock = 0;

    /// <summary>Whether the screen is locked. When Windows will not say, it is taken as not locked.</summary>
    public static bool IsLocked()
    {
        if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, SessionInfoEx, out var buffer, out var bytes))
        {
            return false;
        }

        try
        {
            return bytes >= SessionFlagsOffset + 4
                && Marshal.ReadInt32(buffer, LevelOffset) == 1
                && Marshal.ReadInt32(buffer, SessionFlagsOffset) == SessionStateLock;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
