using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public sealed class StartupEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required RegistryRoot Root { get; init; }
    public bool IsEnabled { get; set; }
    public bool IsProtected { get; init; }
    public string? ProtectedReason { get; init; }

    public string ScopeLabel => Root == RegistryRoot.LocalMachine ? "All users" : "This user";
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

    private static readonly byte[] EnabledFlag = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DisabledFlag = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly LogService _log = log;
    private readonly RegistryHelper _registry = registry;

    public IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();

        entries.AddRange(ScanRoot(RegistryRoot.CurrentUser, RunKeyHkcu, ApprovedHkcu));
        entries.AddRange(ScanRoot(RegistryRoot.LocalMachine, RunKeyHklm, ApprovedHklm));

        _log.Info($"Found {entries.Count} startup entries " +
                  $"({entries.Count(e => e.IsProtected)} protected, " +
                  $"{entries.Count(e => e.IsEnabled && !e.IsProtected)} disableable).");

        return entries.OrderBy(e => e.IsProtected).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<StartupEntry> ScanRoot(RegistryRoot root, string runKey, string approvedKey)
    {
        var baseKey = root == RegistryRoot.LocalMachine
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        using (baseKey)
        {
            using var run = baseKey.OpenSubKey(runKey, writable: false);
            if (run is null)
            {
                yield break;
            }

            using var approved = baseKey.OpenSubKey(approvedKey, writable: false);

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

                var isProtected = SafetyGuard.IsStartupEntryProtected(name, command, out var reason);

                yield return new StartupEntry
                {
                    Name = name,
                    Command = command,
                    Root = root,
                    IsEnabled = enabled,
                    IsProtected = isProtected,
                    ProtectedReason = reason
                };
            }
        }
    }

    /// <summary>
    /// Enables or disables one entry. Returns the registry snapshot for the journal, or null if refused.
    /// </summary>
    public RegistryValueSnapshot? SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.IsProtected)
        {
            _log.Error($"BLOCKED startup change for \"{entry.Name}\" — {entry.ProtectedReason}");
            return null;
        }

        var approvedKey = entry.Root == RegistryRoot.LocalMachine ? ApprovedHklm : ApprovedHkcu;

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
