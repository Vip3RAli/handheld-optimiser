using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A program that starts with Windows.</summary>
/// <param name="ValueName">Its name in the Run key, or its shortcut's file name in the Startup folder.</param>
/// <param name="ForAllUsers">Set up for every user, which only an administrator may change.</param>
internal sealed record StartupApp(string Name, string ValueName, string Command, bool InStartupFolder, bool ForAllUsers, bool Enabled);

/// <summary>
/// The programs that start with Windows, switched on and off with the same flag Task Manager's Startup
/// apps page uses, so the entry itself is kept and Task Manager shows the same thing. This user's own
/// entries can be changed here; entries for all users need the main app, which runs as administrator.
/// </summary>
internal static class StartupApps
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Task Manager's flags: the first byte is 2 for on, and 3 (or 6 on some builds) for off.
    private static readonly byte[] EnabledFlag = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DisabledFlag = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>This user's startup programs, then everyone's, store apps first in each.</summary>
    public static List<StartupApp> Read()
    {
        var apps = new List<StartupApp>();
        apps.AddRange(ReadRun(RegistryHive.CurrentUser, RegistryView.Registry64, "Run"));
        apps.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser));
        apps.AddRange(ReadRun(RegistryHive.LocalMachine, RegistryView.Registry64, "Run"));
        apps.AddRange(ReadRun(RegistryHive.LocalMachine, RegistryView.Registry32, "Run32"));
        apps.AddRange(ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine));

        return apps
            .OrderBy(a => a.ForAllUsers)
            .ThenBy(a => StoreClients.ForCommand(a.Command) is null)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <returns>False when it could not be changed, or belongs to all users.</returns>
    public static bool SetEnabled(StartupApp app, bool enabled)
    {
        if (app.ForAllUsers)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Approved + (app.InStartupFolder ? "StartupFolder" : "Run"));
            key.SetValue(app.ValueName, enabled ? EnabledFlag : DisabledFlag, RegistryValueKind.Binary);
            Program.Log($"Startup program {app.ValueName} switched {(enabled ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not switch the startup program {app.ValueName}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<StartupApp> ReadRun(RegistryHive hive, RegistryView view, string approvedName)
    {
        var found = new List<StartupApp>();
        try
        {
            using var runBase = RegistryKey.OpenBaseKey(hive, view);
            using var approvedBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = runBase.OpenSubKey(Run);
            using var approved = approvedBase.OpenSubKey(Approved + approvedName);

            foreach (var name in run?.GetValueNames() ?? [])
            {
                if (name.Length == 0)
                {
                    continue;
                }

                var command = run!.GetValue(name)?.ToString() ?? string.Empty;
                found.Add(new StartupApp(NameOf(name, command), name, command, InStartupFolder: false,
                    ForAllUsers: hive == RegistryHive.LocalMachine, IsEnabled(approved, name)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // This part of the list is left out.
        }

        return found;
    }

    private static IEnumerable<StartupApp> ReadFolder(string folder, RegistryHive hive)
    {
        var found = new List<StartupApp>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return found;
            }

            using var approvedBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = approvedBase.OpenSubKey(Approved + "StartupFolder");

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(new StartupApp(NameOf(Path.GetFileNameWithoutExtension(file), file), name, file, InStartupFolder: true,
                    ForAllUsers: hive == RegistryHive.LocalMachine, IsEnabled(approved, name)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // This part of the list is left out.
        }

        return found;
    }

    private static bool IsEnabled(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } flag || (flag[0] & 0x01) == 0;

    /// <summary>A store app goes by the store's name, since their Run names vary.</summary>
    private static string NameOf(string name, string command) => StoreClients.ForCommand(command)?.Name ?? name;
}
