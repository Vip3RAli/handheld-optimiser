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
        "\\services\\xboxgipsvc",

        // --- Handheld essentials that server-oriented "safe to disable" lists get wrong ---
        "\\services\\wlidsvc",        // Microsoft account sign-in (Game Pass, Xbox app)
        "\\services\\tokenbroker",    // Web Account Manager, same
        "\\services\\ngcsvc",         // Windows Hello PIN
        "\\services\\ngcctnrsvc",
        "\\services\\wbiosrvc",       // fingerprint reader in the Ally's power button
        "\\services\\bthserv",        // Bluetooth controllers and headsets
        "\\services\\sensorservice",  // ambient light and orientation sensors
        "\\services\\sensrsvc",
        "\\services\\rmsvc",          // airplane mode / radio toggles
        "\\services\\ssdpsrv",        // UPnP discovery, used for NAT traversal in online games
        "\\services\\storsvc",        // installing Game Pass titles to the microSD card
        "\\services\\lfsvc"           // geolocation and automatic time zone (kept at the user's request)
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

        // Framework dependencies: removing these breaks unrelated apps in confusing ways
        "vclibs", "net.native", "ui.xaml", "windowsappruntime",
        "microsoftwindows.client", "windows.shellexperiencehost",
        "windows.startmenuexperiencehost", "windows.search", "windows.cortana.persistentstorage",

        // Media codecs: several games and the Xbox app depend on these
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
        // StartupApproved values are named after the startup entry they switch on or off, so matching
        // the value name would lock any entry called "SecurityHealth" or "ASUS...". They are only
        // enable/disable flags that Task Manager also writes, so the key path alone is vetted there.
        var isStartupFlag = subKey.Contains(@"\Explorer\StartupApproved\", StringComparison.OrdinalIgnoreCase);

        var haystack = (isStartupFlag ? $"{root}\\{subKey}" : $"{root}\\{subKey}\\{valueName}").ToLowerInvariant();

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

    /// <summary>
    /// Service start-type and stop operations go through the same fragments as the registry, since a
    /// service's configuration is its Services\&lt;name&gt; key. One list means one place to protect a service.
    /// </summary>
    public static bool IsServiceProtected(string serviceName, out string? reason)
    {
        if (IsRegistryPathProtected(
                RegistryRoot.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{serviceName}", "Start", out var inner))
        {
            reason = $"Service \"{serviceName}\" is protected. {inner}";
            return true;
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// The only places the maintenance actions may delete files from. An allowlist rather than a
    /// denylist: a cache cleaner that can be pointed at the wrong folder is how people lose data, so
    /// anything not listed here is refused outright.
    /// </summary>
    private static IReadOnlyList<string> DeletableRoots { get; } = BuildDeletableRoots();

    private static List<string> BuildDeletableRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        string[] roots =
        [
            // DirectX and AMD shader caches. The drivers rebuild these on demand. The rest of the AMD
            // folder (Radeon Software settings, ReLive recordings) is deliberately not listed.
            System.IO.Path.Combine(local, "D3DSCache"),
            System.IO.Path.Combine(local, "AMD", "DxCache"),
            System.IO.Path.Combine(local, "AMD", "DxcCache"),
            System.IO.Path.Combine(local, "AMD", "DX9Cache"),
            System.IO.Path.Combine(local, "AMD", "VkCache"),
            System.IO.Path.Combine(local, "AMD", "OglCache"),
            System.IO.Path.Combine(local, "AMD", "GLCache"),
            System.IO.Path.Combine(local, "AMD", "cl.cache"),

            // Crash dumps and error reports.
            System.IO.Path.Combine(windows, "Minidump"),
            System.IO.Path.Combine(windows, "MEMORY.DMP"),
            System.IO.Path.Combine(windows, "LiveKernelReports"),
            System.IO.Path.Combine(local, "CrashDumps"),
            System.IO.Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
            System.IO.Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")
        ];

        return roots.Select(r => System.IO.Path.GetFullPath(r).TrimEnd('\\')).ToList();
    }

    public static IReadOnlyList<string> AllowedDeletionRoots => DeletableRoots;

    public static bool IsDeletionAllowed(string path, out string? reason)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            reason = $"Path could not be resolved ({ex.Message}). Refusing to delete.";
            return false;
        }

        foreach (var root in DeletableRoots)
        {
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                reason = null;
                return true;
            }
        }

        reason = $"\"{full}\" is outside the cache and crash-dump folders this app may clean. Refusing to delete.";
        return false;
    }

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
}
