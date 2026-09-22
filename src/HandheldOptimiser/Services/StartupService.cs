using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public sealed class StartupEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required RegistryRoot Root { get; init; }

    /// <summary>True for entries a 32-bit installer wrote to the WOW6432Node Run key.</summary>
    public bool Is32Bit { get; init; }

    public bool IsEnabled { get; set; }

    public string ScopeLabel => Root == RegistryRoot.LocalMachine
        ? (Is32Bit ? "All users (32-bit)" : "All users")
        : "This user";
}

/// <summary>
/// Reads and toggles classic Run-key startup entries.
///
/// Disabling is done by writing the same StartupApproved flag Task Manager uses rather than deleting the
/// Run value. That keeps the entry intact, makes re-enabling exact, and means an entry we disabled still
/// shows up (as disabled) in Task Manager instead of vanishing confusingly.
/// </summary>
public sealed class StartupService(LogService log, RegistryHelper registry)
{
    private const string RunKeyHkcu = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedHkcu = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // 32-bit entries live under WOW6432Node, but Task Manager keeps their flags in the 64-bit view
    // under Run32 rather than beside them.
    private const string ApprovedHklm32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    private static readonly byte[] EnabledFlag = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DisabledFlag = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly LogService _log = log;
    private readonly RegistryHelper _registry = registry;

    public IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();

        entries.AddRange(ScanRoot(RegistryRoot.CurrentUser, RunKeyHkcu, ApprovedHkcu, is32Bit: false));
        entries.AddRange(ScanRoot(RegistryRoot.LocalMachine, RunKeyHklm, ApprovedHklm, is32Bit: false));
        entries.AddRange(ScanRoot(RegistryRoot.LocalMachine, RunKeyHklm, ApprovedHklm32, is32Bit: true));

        _log.Info($"Found {entries.Count} startup entries ({entries.Count(e => e.IsEnabled)} enabled).");

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<StartupEntry> ScanRoot(RegistryRoot root, string runKey, string approvedKey, bool is32Bit)
    {
        var hive = root == RegistryRoot.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

        // The 32-bit view redirects the Run key to WOW6432Node; approval flags are always in the 64-bit view.
        using var runBase = RegistryKey.OpenBaseKey(hive, is32Bit ? RegistryView.Registry32 : RegistryView.Registry64);
        using var approvedBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        using var run = runBase.OpenSubKey(runKey, writable: false);
        if (run is null)
        {
            yield break;
        }

        using var approved = approvedBase.OpenSubKey(approvedKey, writable: false);

        foreach (var name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var command = run.GetValue(name)?.ToString() ?? string.Empty;

            // First byte 0x02 means enabled; 0x03 (and 0x06 on some builds) means the user disabled it.
            var enabled = true;
            if (approved?.GetValue(name) is byte[] flag && flag.Length > 0)
            {
                enabled = (flag[0] & 0x01) == 0;
            }

            yield return new StartupEntry
            {
                Name = name,
                Command = command,
                Root = root,
                Is32Bit = is32Bit,
                IsEnabled = enabled
            };
        }
    }

    /// <summary>
    /// Enables or disables one entry. Returns the registry snapshot for the journal, or null if it failed.
    /// </summary>
    public RegistryValueSnapshot? SetEnabled(StartupEntry entry, bool enabled)
    {
        var approvedKey = entry.Root != RegistryRoot.LocalMachine ? ApprovedHkcu
            : entry.Is32Bit ? ApprovedHklm32
            : ApprovedHklm;

        var spec = new RegistryValueSpec(
            entry.Root,
            approvedKey,
            entry.Name,
            enabled ? EnabledFlag : DisabledFlag,
            RegistryValueKind.Binary);

        var snapshot = _registry.WriteValue(spec);

        if (snapshot is not null)
        {
            entry.IsEnabled = enabled;
            _log.Success($"Startup entry \"{entry.Name}\" {(enabled ? "enabled" : "disabled")}.");
        }

        return snapshot;
    }
}
