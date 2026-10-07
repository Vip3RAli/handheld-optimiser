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
            "Stops Windows sharing update downloads with other PCs. Updates still download from " +
            "Microsoft as normal. Matters most when docked on a network with other Windows PCs.",
        Category = TweakCategory.Network,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Settings > Windows Update > Delivery Optimization shows \"Some settings are managed by your " +
            "organization\" and its sharing switch is greyed out until you switch this back off.",
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
            "Removes the cap Windows puts on network traffic while audio is playing, which is always the " +
            "case in a game. Can smooth out ping spikes.",
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
            "Stops the driver that counts per-app network usage, a known cause of memory growth over " +
            "long sessions.",
        Category = TweakCategory.Network,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        Warning =
            "Data usage in Settings and Task Manager's per-app network history stop updating, and " +
            "metered-connection data limits are no longer tracked.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Ndu", "Start", 4, RegistryValueKind.DWord)
        ]
    };
}
