using Microsoft.Win32;

namespace HandheldOptimiser.Models;

public enum RegistryRoot
{
    LocalMachine,
    CurrentUser,
    ClassesRoot,
    Users
}

/// <summary>
/// A single registry value a tweak wants to own, expressed as "this is the value I want it to have".
/// </summary>
public sealed record RegistryValueSpec(
    RegistryRoot Root,
    string SubKey,
    string ValueName,
    object DesiredValue,
    RegistryValueKind Kind = RegistryValueKind.DWord)
{
    public string DisplayPath => $"{RootShortName}\\{SubKey}\\{ValueName}";

    public string RootShortName => Root switch
    {
        RegistryRoot.LocalMachine => "HKLM",
        RegistryRoot.CurrentUser => "HKCU",
        RegistryRoot.ClassesRoot => "HKCR",
        RegistryRoot.Users => "HKU",
        _ => Root.ToString()
    };
}

/// <summary>
/// What a registry value looked like before we touched it, so the change can be undone exactly —
/// including undoing it back to "this value did not exist", which is not the same as "it was zero".
/// </summary>
public sealed class RegistryValueSnapshot
{
    public RegistryRoot Root { get; set; }
    public string SubKey { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;

    public bool KeyExisted { get; set; }
    public bool ValueExisted { get; set; }

    /// <summary>Serialised form of the original value; null when the value did not exist.</summary>
    public string? OriginalValue { get; set; }
    public RegistryValueKind OriginalKind { get; set; } = RegistryValueKind.Unknown;
}
