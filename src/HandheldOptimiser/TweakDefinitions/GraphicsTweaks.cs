using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// How Windows schedules the GPU and prioritises the foreground game.
/// </summary>
public static class GraphicsTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        GameMode,
        HardwareScheduling
    ];

    /// <summary>
    /// AutoGameModeEnabled is the Settings switch itself; AllowAutoGameMode is the older companion value.
    /// Both are set so the result is the same whichever one a given build reads.
    /// </summary>
    public static Tweak GameMode => new()
    {
        Id = "graphics.gamemode",
        Name = "Force Game Mode on",
        Description =
            "Makes sure Game Mode is enabled, so Windows prioritises the running game and stops Windows Update " +
            "installing drivers or showing restart prompts while you play.",
        Category = TweakCategory.Graphics,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord)
        ]
    };

    public static Tweak HardwareScheduling => new()
    {
        Id = "graphics.hags",
        Name = "Enable hardware-accelerated GPU scheduling",
        Description =
            "Lets the GPU manage its own memory and work queue instead of the CPU doing it. Lowers input " +
            "latency slightly, and frame generation in some games needs it switched on.",
        Category = TweakCategory.Graphics,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Results vary by game and driver. If you notice new stutter after restarting, switch this back off.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord)
        ]
    };
}
