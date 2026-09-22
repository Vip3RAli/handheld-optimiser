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
        Name = "Maximise foreground priority (Win32PrioritySeparation)",
        Description =
            "Tells the scheduler to give the foreground window short, frequent time slices with the " +
            "largest priority boost Windows allows. The game you are playing gets the CPU ahead of " +
            "background work. Takes effect immediately.",
        Category = TweakCategory.CpuKernel,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord)
        ]
    };
}
