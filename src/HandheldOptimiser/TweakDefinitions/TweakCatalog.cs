using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Every tweak the app has, in the order the sets are listed here. Kept apart from the engine so that
/// what exists can be read, and tested, without building anything that touches the system.
/// </summary>
public static class TweakCatalog
{
    public static IReadOnlyList<Tweak> All { get; } =
    [
        .. GamingTweaks.All,
        .. DebloatTweaks.All,
        .. CpuKernelTweaks.All,
        .. NetworkTweaks.All,
        .. ServiceTweaks.All,
        .. ArmouryCrateTweaks.All,
        .. InterfaceTweaks.All,
        .. StorageTweaks.All,
        .. GraphicsTweaks.All,
        .. UsabilityTweaks.All,
        .. FullScreenTweaks.All,
        .. SleepTweaks.All
    ];
}
