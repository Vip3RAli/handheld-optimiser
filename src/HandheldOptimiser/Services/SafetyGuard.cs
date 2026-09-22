using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// Central veto on every mutating operation. Nothing in this app writes to the registry, touches a
/// service, or removes a package without passing through here first.
///
/// This exists because "we just won't add a tweak that breaks Defender" is a promise about the code as
/// written, not about the code as it will be six months from now. A denylist that runs at execution
/// time keeps holding after someone adds a tweak carelessly.
/// </summary>
public static class SafetyGuard
{
    /// <summary>
    /// Registry path fragments that may never be written. Matched case-insensitively as substrings,
    /// which is deliberately over-broad: a false positive refuses one tweak and says so in the log,
    /// while a false negative breaks Windows Update on someone's handheld.
    /// </summary>
    private static readonly string[] ProtectedRegistryFragments =
    [
        // --- Defender / security stack ---
        "windows defender",
        "windowsdefender",
        "microsoft\\windows security health",
        "securityhealth",
        "\\services\\windefend",
        "\\services\\wdnissvc",
        "\\services\\wdfilter",
        "\\services\\wdboot",
        "\\services\\sense",
        "\\services\\wscsvc",
        "\\services\\securityhealthservice",
        "\\services\\mpssvc",
        "\\services\\bfe",
        "smartscreen",
        "\\policies\\microsoft\\windows\\deviceguard\\credentialguard",

        // --- Windows Update and its dependencies ---
        "windowsupdate",
        "windows update",
        "\\services\\wuauserv",
        "\\services\\usosvc",
        "\\services\\waasmedicsvc",
        "\\services\\bits",
        "\\services\\trustedinstaller",
        "\\services\\msiserver",
        "\\services\\cryptsvc",
        "\\services\\dosvc",
        "deliveryoptimization",
        "\\waas\\",

        // --- ASUS / Armoury Crate SE ---
        "asus",
        "armoury",
        "armourycrate",
        "rog live service",
        "lightingservice",

        // --- AMD graphics / chipset ---
        "\\amd\\",
        "\\services\\amd",
        "amdryzen",
        "ati technologies",
        "\\services\\amdppm",
        "\\services\\amdkmdag",

        // --- Realtek / core audio ---
        "realtek",
        "\\services\\rtk",
        "\\services\\audiosrv",
        "\\services\\audioendpointbuilder",

        // --- Xbox / Game Pass entitlement plumbing (Game DVR is tweaked elsewhere, this is the store side) ---
        "\\services\\gamingservices",
        "\\services\\xblauthmanager",
        "\\services\\xblgamesave",
        "\\services\\xboxnetapisvc",
        "\\services\\xboxgipsvc"
    ];

    /// <summary>
    /// Appx identity-name fragments that must survive debloat. Matched case-insensitively.
    /// </summary>
    private static readonly string[] ProtectedAppxFragments =
    [
        // Vendor
        "asus", "armoury", "rog", "myasus",
        "amd", "realtek", "nvidia", "intel",

        // Security surface
        "sechealthui", "windows.security", "windowsdefender",

        // Store + installer plumbing (removing these makes reinstalling anything a nightmare)
        "windowsstore", "storepurchaseapp", "desktopappinstaller",

        // Game Pass / Xbox: user explicitly wants these kept working
        "gamingapp", "gamingservices", "xboxidentityprovider",
        "xbox.tcui", "xboxgameoverlay", "xboxgamingoverlay", "xboxspeechtotextoverlay",

        // Framework dependencies — removing these breaks unrelated apps in confusing ways
        "vclibs", "net.native", "ui.xaml", "windowsappruntime",
        "microsoftwindows.client", "windows.shellexperiencehost",
        "windows.startmenuexperiencehost", "windows.search", "windows.cortana.persistentstorage",

        // Media codecs — several games and the Xbox app depend on these
        "heifimageextension", "hevcvideoextension", "vp9videoextensions",
        "webmediaextensions", "webpimageextension", "av1videoextension",
        "rawimageextension"
    ];

    /// <summary>
    /// Windows optional features that may never be disabled, regardless of what a tweak asks for.
    /// </summary>
    private static readonly string[] ProtectedOptionalFeatureFragments =
    [
        "defender", "smartscreen", "netfx", "windowsupdate"
    ];

    public static bool IsRegistryPathProtected(RegistryRoot root, string subKey, string valueName, out string? reason)
    {
        var haystack = $"{root}\\{subKey}\\{valueName}".ToLowerInvariant();

        foreach (var fragment in ProtectedRegistryFragments)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
            {
                reason = $"Registry path is protected (matched \"{fragment}\"). Refusing to write.";
                return true;
            }
        }

        reason = null;
        return false;
    }

    public static bool IsRegistryPathProtected(RegistryValueSpec spec, out string? reason) =>
        IsRegistryPathProtected(spec.Root, spec.SubKey, spec.ValueName, out reason);

    public static bool IsAppxProtected(string packageIdentityName, out string? reason)
    {
        var lower = packageIdentityName.ToLowerInvariant();

        foreach (var fragment in ProtectedAppxFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
            {
                reason = $"Package is protected (matched \"{fragment}\"). Refusing to remove.";
                return true;
            }
        }

        reason = null;
        return false;
    }

    public static bool IsOptionalFeatureProtected(string featureName, out string? reason)
    {
        var lower = featureName.ToLowerInvariant();

        foreach (var fragment in ProtectedOptionalFeatureFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
            {
                reason = $"Optional feature is protected (matched \"{fragment}\"). Refusing to disable.";
                return true;
            }
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// Startup entries whose names indicate they belong to hardware that must keep working.
    /// Used to lock rows in the startup manager so they cannot be unchecked.
    /// </summary>
    public static bool IsStartupEntryProtected(string name, string command, out string? reason)
    {
        var haystack = $"{name} {command}".ToLowerInvariant();

        string[] fragments =
        [
            "asus", "armoury", "armourycrate", "rog", "myasus", "acse",
            "amd", "realtek", "rtk", "nvidia",
            "securityhealth", "windows defender", "msmpeng",
            "onedrive setup" // leave OneDrive's own uninstall/setup stub alone
        ];

        foreach (var fragment in fragments)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
            {
                reason = $"Belongs to protected hardware/security software (matched \"{fragment}\").";
                return true;
            }
        }

        reason = null;
        return false;
    }
}
