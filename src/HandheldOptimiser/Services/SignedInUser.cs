using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace HandheldOptimiser.Services;

/// <summary>
/// The person signed in to Windows, when that is not the account this app runs as.
///
/// The app runs as administrator. Someone signed in with a standard account approves the prompt with an
/// administrator's name and password, and the app then runs as that administrator: HKEY_CURRENT_USER
/// is the administrator's registry, not theirs, and a per-user tweak would change the wrong account.
/// In that case the signed-in person's own registry is opened instead, from HKEY_USERS. Windows keeps
/// it loaded while they are signed in, and administrators may write to it.
///
/// Only the registry follows them. App packages, Windows settings changed through a system call, and
/// the app's own logs and undo journal still belong to the account the app runs as.
/// </summary>
public static class SignedInUser
{
    private static readonly Lazy<Accounts> Found = new(Find);

    /// <summary>
    /// The SID of the person signed in, when it is not this app's own account and their registry is
    /// loaded, or null when HKEY_CURRENT_USER is already theirs.
    /// </summary>
    public static string? OtherSid => Found.Value.OtherSid;

    /// <summary>The account this app runs as, as DOMAIN\name, for the log.</summary>
    public static string? ProcessAccount => Found.Value.ProcessAccount;

    /// <summary>The account signed in to this session, as DOMAIN\name, for the log.</summary>
    public static string? SessionAccount => Found.Value.SessionAccount;

    /// <summary>
    /// The signed-in person's registry: HKEY_CURRENT_USER when the app runs as them, or their hive under
    /// HKEY_USERS when it does not. Dispose it as a base key.
    /// </summary>
    public static RegistryKey OpenRegistry(RegistryView view)
    {
        if (OtherSid is { } sid)
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);

            // Opened writable, since a subkey cannot be created under a key opened only to read.
            if (users.OpenSubKey(sid, writable: true) is { } hive)
            {
                return hive;
            }
        }

        return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
    }

    /// <summary>
    /// Which SID's registry to open in place of HKEY_CURRENT_USER, or null for HKEY_CURRENT_USER itself.
    /// Only when both accounts are known, they differ, and the signed-in one's registry is loaded.
    /// </summary>
    public static string? ChooseOther(string? processSid, string? sessionSid, Func<string, bool> isLoaded)
    {
        if (string.IsNullOrEmpty(processSid) || string.IsNullOrEmpty(sessionSid))
        {
            return null;
        }

        if (string.Equals(processSid, sessionSid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return isLoaded(sessionSid) ? sessionSid : null;
    }

    private sealed record Accounts(string? OtherSid, string? ProcessAccount, string? SessionAccount);

    private static Accounts Find()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var processSid = identity.User?.Value;
        var sessionAccount = SessionAccountName();
        var sessionSid = sessionAccount is null ? null : SidOf(sessionAccount);

        var other = ChooseOther(processSid, sessionSid, IsLoaded);
        return new Accounts(other, identity.Name, sessionAccount);
    }

    private static bool IsLoaded(string sid)
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var hive = users.OpenSubKey(sid);
            return hive is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? SidOf(string account)
    {
        try
        {
            return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException when the name has no account, or a Win32 error from the lookup.
            return null;
        }
    }

    /// <summary>DOMAIN\name of whoever is signed in to the session this app runs in, or null if Windows will not say.</summary>
    private static string? SessionAccountName()
    {
        var user = SessionText(WtsUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = SessionText(WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    private static string? SessionText(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, CurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // WTS_CURRENT_SESSION, and the WTS_INFO_CLASS values for the user and domain names.
    private const int CurrentSession = -1;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
