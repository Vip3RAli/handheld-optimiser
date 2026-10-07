using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Microsoft.Win32;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The protected lists in <see cref="SafetyGuard"/>. Pure lookups: nothing here reads or writes the
/// registry, services or packages.
/// </summary>
public sealed class SafetyGuardTests
{
    [Theory]
    // Defender and the firewall
    [InlineData("WinDefend")]
    [InlineData("WdNisSvc")]
    [InlineData("Sense")]
    [InlineData("SecurityHealthService")]
    [InlineData("wscsvc")]
    [InlineData("mpssvc")]
    [InlineData("BFE")]
    // Windows Update and what it needs
    [InlineData("wuauserv")]
    [InlineData("UsoSvc")]
    [InlineData("WaaSMedicSvc")]
    [InlineData("BITS")]
    [InlineData("TrustedInstaller")]
    [InlineData("msiserver")]
    [InlineData("CryptSvc")]
    [InlineData("DoSvc")]
    // Game Pass and Xbox sign-in
    [InlineData("GamingServices")]
    [InlineData("XblAuthManager")]
    [InlineData("XblGameSave")]
    [InlineData("XboxNetApiSvc")]
    [InlineData("XboxGipSvc")]
    [InlineData("wlidsvc")]
    [InlineData("TokenBroker")]
    // Handheld hardware
    [InlineData("WbioSrvc")]
    [InlineData("bthserv")]
    [InlineData("SensorService")]
    [InlineData("Audiosrv")]
    [InlineData("AudioEndpointBuilder")]
    [InlineData("StorSvc")]
    [InlineData("AsusAppService")]
    [InlineData("amdfendr")]
    public void Protected_services_are_refused(string service)
    {
        Assert.True(SafetyGuard.IsServiceProtected(service, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("WINDEFEND")]
    [InlineData("windefend")]
    [InlineData("WuAuServ")]
    public void Service_matching_ignores_case(string service) =>
        Assert.True(SafetyGuard.IsServiceProtected(service, out _));

    /// <summary>Every service the Services page disables. If one of these becomes protected, its tweak silently stops working.</summary>
    [Theory]
    [InlineData("SysMain")]
    [InlineData("WSearch")]
    [InlineData("Spooler")]
    [InlineData("Fax")]
    [InlineData("PcaSvc")]
    [InlineData("TrkWks")]
    [InlineData("RetailDemo")]
    [InlineData("WalletService")]
    [InlineData("SEMgrSvc")]
    [InlineData("PhoneSvc")]
    [InlineData("MapsBroker")]
    [InlineData("SharedAccess")]
    [InlineData("icssvc")]
    [InlineData("wisvc")]
    [InlineData("WpcMonSvc")]
    [InlineData("WMPNetworkSvc")]
    [InlineData("AxInstSV")]
    public void Services_the_app_disables_are_not_protected(string service)
    {
        Assert.False(SafetyGuard.IsServiceProtected(service, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate")]
    [InlineData(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start")]
    [InlineData(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AppHost", "EnableWebContentEvaluation_SmartScreen")]
    [InlineData(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxCacheSize")]
    public void Protected_registry_paths_are_refused(RegistryRoot root, string subKey, string valueName) =>
        Assert.True(SafetyGuard.IsRegistryPathProtected(root, subKey, valueName, out _));

    [Fact]
    public void Msi_interrupt_values_are_not_mistaken_for_Windows_Installer() =>
        Assert.False(SafetyGuard.IsRegistryPathProtected(
            RegistryRoot.LocalMachine,
            @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_1002\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
            "MSISupported",
            out _));

    [Fact]
    public void Startup_flags_are_vetted_by_key_path_not_value_name()
    {
        // The StartupApproved value is named after the entry, so "SecurityHealth" here is only a flag.
        Assert.False(SafetyGuard.IsRegistryPathProtected(
            RegistryRoot.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            "SecurityHealth",
            out _));

        // The same name anywhere else is still caught.
        Assert.True(SafetyGuard.IsRegistryPathProtected(
            RegistryRoot.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            "SecurityHealth",
            out _));
    }

    private const string DeliveryOptimizationPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    [Fact]
    public void Update_sharing_policy_may_only_be_written_as_zero()
    {
        Assert.False(SafetyGuard.IsRegistryPathProtected(
            new RegistryValueSpec(RegistryRoot.LocalMachine, DeliveryOptimizationPolicy, "DODownloadMode", 0), out _));

        Assert.True(SafetyGuard.IsRegistryPathProtected(
            new RegistryValueSpec(RegistryRoot.LocalMachine, DeliveryOptimizationPolicy, "DODownloadMode", 100), out _));

        Assert.True(SafetyGuard.IsRegistryPathProtected(
            new RegistryValueSpec(RegistryRoot.LocalMachine, DeliveryOptimizationPolicy, "DODownloadMode", "0", RegistryValueKind.String), out _));
    }

    [Fact]
    public void Update_sharing_policy_can_be_restored_whatever_it_was()
    {
        // The path-only check is what revert uses, and undo has to be able to put back any prior value.
        Assert.False(SafetyGuard.IsRegistryPathProtected(
            RegistryRoot.LocalMachine, DeliveryOptimizationPolicy, "DODownloadMode", out _));

        // Only for HKLM; the exemption is exact.
        Assert.True(SafetyGuard.IsRegistryPathProtected(
            RegistryRoot.CurrentUser, DeliveryOptimizationPolicy, "DODownloadMode", out _));
    }

    private static IEnumerable<Tweak> AllTweaks() =>
    [
        .. GamingTweaks.All,
        .. DebloatTweaks.All,
        .. CpuKernelTweaks.All,
        .. NetworkTweaks.All,
        .. ServiceTweaks.All,
        .. InterfaceTweaks.All,
        .. StorageTweaks.All,
        .. GraphicsTweaks.All,
        .. UsabilityTweaks.All,
        .. FullScreenTweaks.All,
        .. SleepTweaks.All
    ];

    public static TheoryData<string, string> TweakRegistryValues()
    {
        var data = new TheoryData<string, string>();
        foreach (var tweak in AllTweaks())
        {
            foreach (var spec in tweak.RegistryValues.Concat(tweak.ScriptRegistryValues))
            {
                data.Add(tweak.Id, spec.DisplayPath);
            }
        }
        return data;
    }

    /// <summary>
    /// A tweak that writes a protected value would be refused at run time and never work. This catches
    /// it at build time instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(TweakRegistryValues))]
    public void Every_tweak_value_passes_the_guard(string tweakId, string displayPath)
    {
        var spec = AllTweaks()
            .Single(t => t.Id == tweakId)
            .RegistryValues.Concat(AllTweaks().Single(t => t.Id == tweakId).ScriptRegistryValues)
            .First(s => s.DisplayPath == displayPath);

        Assert.False(SafetyGuard.IsRegistryPathProtected(spec, out var reason), reason);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxGamingOverlay")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.HEVCVideoExtension")]
    [InlineData("B9ECED6F.ArmouryCrateSE")]
    [InlineData("AdvancedMicroDevicesInc-2.AMDRadeonSoftware")]
    public void Protected_packages_are_refused(string identityName) =>
        Assert.True(SafetyGuard.IsAppxProtected(identityName, out _));

    public static TheoryData<string> CatalogPackages()
    {
        var data = new TheoryData<string>();
        foreach (var target in AppxCatalog.All)
        {
            data.Add(target.IdentityName);
        }
        return data;
    }

    /// <summary>Everything on the Bloatware page has to be removable, or ticking it does nothing.</summary>
    [Theory]
    [MemberData(nameof(CatalogPackages))]
    public void Catalog_packages_are_not_protected(string identityName) =>
        Assert.False(SafetyGuard.IsAppxProtected(identityName, out var reason), reason);

    [Theory]
    [InlineData("Windows-Defender-Default-Definitions", true)]
    [InlineData("NetFx3", true)]
    [InlineData("NetFx4-AdvSrvs", true)]
    [InlineData("Printing-XPSServices-Features", false)]
    [InlineData("WorkFolders-Client", false)]
    public void Optional_feature_protection(string feature, bool isProtected) =>
        Assert.Equal(isProtected, SafetyGuard.IsOptionalFeatureProtected(feature, out _));

    [Fact]
    public void Deletion_is_allowed_only_inside_the_cache_folders()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(local, "D3DSCache", "abc"), out _));
        Assert.True(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(local, "AMD", "DxCache"), out _));

        Assert.False(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(local, "AMD"), out _));
        Assert.False(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(windows, "System32"), out _));
        Assert.False(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(local, "D3DSCacheExtra"), out _));
        Assert.False(SafetyGuard.IsDeletionAllowed(System.IO.Path.Combine(local, "D3DSCache", "..", "..", "Roaming"), out _));
    }
}
