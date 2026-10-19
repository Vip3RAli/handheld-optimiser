using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Scheduler settings. Reversible; nothing here touches drivers or services.
/// </summary>
public static class CpuKernelTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        PrioritySeparation
    ];

    /// <summary>
    /// 0x26 = short, variable quanta with the maximum (3:1) foreground boost. The Windows client default
    /// is 0x2, which already favours the foreground but with a smaller boost.
    /// </summary>
    public static Tweak PrioritySeparation => new()
    {
        Id = "cpu.priorityseparation",
        Name = "Give the game priority over background apps",
        Description =
            "Gives the game in the foreground the CPU ahead of background work. Sets the " +
            "Win32PrioritySeparation value. Takes effect immediately.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord)
        ]
    };
}
