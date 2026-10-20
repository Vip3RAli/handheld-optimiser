using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Stops Windows interrupting a game with prompts or background throttling. The accessibility Flags
/// values are bitmasks stored as strings; 506 and 122 are the defaults (510 and 126) with the
/// "hotkey active" bit cleared.
/// </summary>
public static class UsabilityTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        StickyKeys,
        FilterKeys,
        PowerThrottling
    ];

    public static Tweak StickyKeys => new()
    {
        Id = "usability.stickykeys",
        Name = "Turn off the Sticky Keys shortcut",
        Description =
            "Stops pressing Shift five times from opening the Sticky Keys prompt, which can drop you out " +
            "of a game.",
        Category = TweakCategory.TouchAndDesktop,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", RegistryValueKind.String)
        ]
    };

    public static Tweak FilterKeys => new()
    {
        Id = "usability.filterkeys",
        Name = "Turn off the Filter Keys shortcut",
        Description =
            "Stops holding right Shift for eight seconds from turning on Filter Keys, which makes the " +
            "keyboard ignore quick presses.",
        Category = TweakCategory.TouchAndDesktop,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String)
        ]
    };

    public static Tweak PowerThrottling => new()
    {
        Id = "usability.powerthrottling",
        Name = "Turn off power throttling",
        Description =
            "Stops Windows slowing down background apps, so overlays and launchers stay at full speed " +
            "while you play.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Background apps run at full power, which shortens battery life. Best left off if you mostly " +
            "play on battery.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff", 1, RegistryValueKind.DWord)
        ]
    };
}
