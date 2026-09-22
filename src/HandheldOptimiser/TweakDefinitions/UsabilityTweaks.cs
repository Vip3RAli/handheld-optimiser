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
        Name = "Disable Sticky Keys shortcut",
        Description =
            "Stops pressing Shift five times from popping up the Sticky Keys prompt and dropping you out of a " +
            "full-screen game, which is easy to trigger when Shift is mapped to a controller button.",
        Category = TweakCategory.Usability,
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
        Name = "Disable Filter Keys shortcut",
        Description =
            "Stops holding right Shift for eight seconds from turning on Filter Keys, which makes the keyboard " +
            "and mapped buttons ignore quick presses.",
        Category = TweakCategory.Usability,
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
        Name = "Disable power throttling",
        Description =
            "Stops Windows slowing down apps it thinks are in the background. Keeps overlays, launchers and " +
            "second-screen apps at full speed while a game is in focus.",
        Category = TweakCategory.Usability,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Background apps run at full power too, which shortens battery life when unplugged. Best left " +
            "off if you mostly play on battery.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff", 1, RegistryValueKind.DWord)
        ]
    };
}
