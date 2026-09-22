using System.IO;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Registry reads and writes, with every write gated by <see cref="SafetyGuard"/> and preceded by a
/// snapshot of the prior state so the change can be reversed exactly.
/// </summary>
public sealed class RegistryHelper(LogService log)
{
    private readonly LogService _log = log;

    private static RegistryKey OpenRoot(RegistryRoot root) => root switch
    {
        RegistryRoot.LocalMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegistryRoot.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        RegistryRoot.ClassesRoot => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        RegistryRoot.Users => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => throw new ArgumentOutOfRangeException(nameof(root))
    };

    public object? ReadValue(RegistryRoot root, string subKey, string valueName)
    {
        try
        {
            using var baseKey = OpenRoot(root);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warning($"Could not read {root}\\{subKey}\\{valueName}: {ex.Message}");
            return null;
        }
    }

    public bool ValueMatches(RegistryValueSpec spec)
    {
        var current = ReadValue(spec.Root, spec.SubKey, spec.ValueName);
        if (current is null)
        {
            return false;
        }

        // Registry DWORDs come back as int, QWORDs as long; compare on string form to avoid
        // boxing-comparison surprises between int/long/uint.
        return string.Equals(
            NormaliseForComparison(current),
            NormaliseForComparison(spec.DesiredValue),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseForComparison(object value) => value switch
    {
        string[] multi => string.Join("\u0000", multi),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString() ?? string.Empty
    };

    public RegistryValueSnapshot Snapshot(RegistryRoot root, string subKey, string valueName)
    {
        var snapshot = new RegistryValueSnapshot
        {
            Root = root,
            SubKey = subKey,
            ValueName = valueName
        };

        try
        {
            using var baseKey = OpenRoot(root);
            using var key = baseKey.OpenSubKey(subKey, writable: false);

            if (key is null)
            {
                snapshot.KeyExisted = false;
                snapshot.ValueExisted = false;
                return snapshot;
            }

            snapshot.KeyExisted = true;

            var existing = key.GetValue(valueName);
            if (existing is null)
            {
                snapshot.ValueExisted = false;
                return snapshot;
            }

            snapshot.ValueExisted = true;
            snapshot.OriginalKind = key.GetValueKind(valueName);
            snapshot.OriginalValue = SerialiseValue(existing, snapshot.OriginalKind);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warning($"Could not snapshot {root}\\{subKey}\\{valueName}: {ex.Message}");
        }

        return snapshot;
    }

    private static string SerialiseValue(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        RegistryValueKind.MultiString => string.Join("\u0000", (string[])value),
        _ => value.ToString() ?? string.Empty
    };

    private static object DeserialiseValue(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)long.Parse(value)),
        RegistryValueKind.QWord => long.Parse(value),
        RegistryValueKind.Binary => Convert.FromBase64String(value),
        RegistryValueKind.MultiString => value.Split('\u0000', StringSplitOptions.None),
        _ => value
    };

    /// <summary>
    /// Writes a value, first capturing what was there. Returns null if the write was refused or failed;
    /// otherwise returns the snapshot to be journalled.
    /// </summary>
    public RegistryValueSnapshot? WriteValue(RegistryValueSpec spec)
    {
        if (SafetyGuard.IsRegistryPathProtected(spec, out var reason))
        {
            _log.Error($"BLOCKED {spec.DisplayPath} — {reason}");
            return null;
        }

        var snapshot = Snapshot(spec.Root, spec.SubKey, spec.ValueName);

        try
        {
            using var baseKey = OpenRoot(spec.Root);
            using var key = baseKey.CreateSubKey(spec.SubKey, writable: true)
                ?? throw new IOException($"CreateSubKey returned null for {spec.SubKey}");

            key.SetValue(spec.ValueName, spec.DesiredValue, spec.Kind);

            var before = snapshot.ValueExisted ? snapshot.OriginalValue : "(absent)";
            _log.Success($"SET {spec.DisplayPath} = {spec.DesiredValue} [{spec.Kind}] (was {before})");

            return snapshot;
        }
        catch (Exception ex)
        {
            _log.Error($"FAILED to set {spec.DisplayPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts a value back exactly as recorded, including deleting it again if it did not exist before.
    /// </summary>
    public bool RestoreSnapshot(RegistryValueSnapshot snapshot)
    {
        if (SafetyGuard.IsRegistryPathProtected(snapshot.Root, snapshot.SubKey, snapshot.ValueName, out var reason))
        {
            _log.Error($"BLOCKED restore of {snapshot.Root}\\{snapshot.SubKey}\\{snapshot.ValueName} — {reason}");
            return false;
        }

        var display = $"{snapshot.Root}\\{snapshot.SubKey}\\{snapshot.ValueName}";

        try
        {
            using var baseKey = OpenRoot(snapshot.Root);

            if (!snapshot.ValueExisted)
            {
                using var key = baseKey.OpenSubKey(snapshot.SubKey, writable: true);
                if (key is null)
                {
                    _log.Trace($"RESTORE {display}: key already absent, nothing to undo");
                    return true;
                }

                if (key.GetValue(snapshot.ValueName) is not null)
                {
                    key.DeleteValue(snapshot.ValueName, throwOnMissingValue: false);
                }

                _log.Success($"RESTORE {display} -> removed (did not exist originally)");
                return true;
            }

            using var writeKey = baseKey.CreateSubKey(snapshot.SubKey, writable: true)
                ?? throw new IOException($"CreateSubKey returned null for {snapshot.SubKey}");

            var value = DeserialiseValue(snapshot.OriginalValue ?? string.Empty, snapshot.OriginalKind);
            writeKey.SetValue(snapshot.ValueName, value, snapshot.OriginalKind);

            _log.Success($"RESTORE {display} = {snapshot.OriginalValue} [{snapshot.OriginalKind}]");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"FAILED to restore {display}: {ex.Message}");
            return false;
        }
    }
}
