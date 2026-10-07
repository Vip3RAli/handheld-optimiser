using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// The registry operations a tweak is allowed to use. <see cref="RegistryHelper"/> is the only real
/// implementation; the interface exists so the apply and revert logic can be tested against an
/// in-memory registry instead of the machine's own.
/// </summary>
public interface IRegistryStore
{
    object? ReadValue(RegistryRoot root, string subKey, string valueName);
    bool ValueMatches(RegistryValueSpec spec);
    RegistryValueSnapshot? WriteValue(RegistryValueSpec spec);
    bool RestoreSnapshot(RegistryValueSnapshot snapshot);
}
