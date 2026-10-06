using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

public static class NetworkTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        NetworkThrottling,
        Ndu,
        UpdateSharing
    ];

    /// <summary>
    /// DODownloadMode 0 is "HTTP only": Delivery Optimization still downloads updates from Microsoft, it
    /// just stops trading pieces with other PCs. Everything else about Delivery Optimization is behind
    /// <see cref="Services.SafetyGuard"/>, which lets this one value through and nothing else.
    /// </summary>
    public static Tweak UpdateSharing => new()
    {
        Id = "network.updatesharing",
        Name = "Turn off update sharing with other PCs",
        Description =
            "Stops Windows sending and fetching pieces of Windows and Store updates to and from other PCs. " +
            "Updates still download from Microsoft as normal. Windows only shares on mains power, and by " +
            "default only with PCs on your own network, so this matters most when the handheld is docked " +
            "alongside other Windows PCs.",
        Category = TweakCategory.Network,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "This is set as a policy, so Settings > Windows Update > Delivery Optimization shows \"Some " +
            "settings are managed by your organization\" and its sharing switch is greyed out until you " +
            "switch this back off.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
                "DODownloadMode", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// The multimedia scheduler caps non-multimedia network packet processing at 10 packets/ms while
    /// audio or video is playing. 0xFFFFFFFF removes the cap. Stored as a DWORD, so it is written as -1.
    /// </summary>
    public static Tweak NetworkThrottling => new()
    {
        Id = "network.throttling",
        Name = "Disable network throttling",
        Description =
            "Removes the Multimedia Class Scheduler's cap on network packet processing while audio is " +
            "playing, which is always the case in a game. Can smooth out ping spikes in online play.",
        Category = TweakCategory.Network,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Ndu is a kernel driver, not a service, so its start type lives only in the registry and there is
    /// nothing to stop at runtime; it unloads on the next boot.
    /// </summary>
    public static Tweak Ndu => new()
    {
        Id = "network.ndu",
        Name = "Disable Network Data Usage monitor (Ndu)",
        Description =
            "Stops the driver that counts per-app network usage. It is a known source of non-paged pool " +
            "memory growth over long sessions.",
        Category = TweakCategory.Network,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        Warning =
            "Settings > Network > Data usage and Task Manager's per-app network history will stop updating. " +
            "Metered-connection data limits will no longer be tracked.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Ndu", "Start", 4, RegistryValueKind.DWord)
        ]
    };
}
