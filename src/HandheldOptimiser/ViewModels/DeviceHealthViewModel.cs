using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// Read-only verification page. Below the protected components it shows what Windows reports about
/// sleep, the power plans and storage, which changes nothing either.
///
/// This app is forbidden from disabling the handheld vendor's software, the AMD, Intel and Realtek
/// drivers, Defender or Windows Update, so this page has no toggles at all. Its job is to show that those
/// components are present and healthy, and to repair them if some other debloat tool got there first.
/// Which vendor it inspects follows the device: Armoury Crate on a ROG Ally, Legion Space on a Legion Go,
/// MSI Center M on an MSI Claw.
/// </summary>
public sealed partial class DeviceHealthViewModel(SystemStateService systemState, LogService log, IShell shell)
    : PageViewModelBase(shell)
{
    private readonly SystemStateService _systemState = systemState;
    private readonly LogService _log = log;

    public override string Title => "Device & System Health";
    public override string Glyph => "";
    public override string Subtitle => "Protected components, sleep, power plans and storage, as Windows reports them. No toggles here by design.";

    /// <summary>The rows marked yellow or red, lifted to the top of the page with their explanation.</summary>
    public ObservableCollection<HealthRowViewModel> Attention { get; } = [];

    public ObservableCollection<HealthSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    private string _hardwareModel = "Detecting…";

    /// <summary>The first thing read on the page: how many rows need a look, or that none does.</summary>
    [ObservableProperty]
    private string _attentionText = "Checking…";

    /// <summary>The colour of that line: the worst status found, and grey until the first check answers.</summary>
    [ObservableProperty]
    private HealthStatus _worstStatus = HealthStatus.Unknown;

    [ObservableProperty]
    private string _fineText = string.Empty;

    [ObservableProperty]
    private bool _hasAttention;

    private bool _isChecking;

    public override Task LoadAsync() => Sections.Count == 0 ? RefreshAsync() : Task.CompletedTask;

    /// <summary>Runs the checks in the background. Only a repair holds the app busy.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;

        try
        {
            await Shell.RunScanAsync("device health", LoadChecksAsync);
        }
        finally
        {
            _isChecking = false;
        }
    }

    private async Task LoadChecksAsync(CancellationToken ct)
    {
        HardwareModel = _systemState.GetHardwareModel();

        var results = await _systemState.RunHealthChecksAsync(ct);
        var page = HealthLayout.Build(results, ProtectionNoteFor(_systemState.GetVendorSoftware()));

        // A re-check keeps open what the user had opened.
        var open = Sections.Where(s => s.IsExpanded).Select(s => s.Title).ToHashSet();

        Attention.Clear();
        foreach (var row in page.Attention)
        {
            Attention.Add(row);
        }

        Sections.Clear();
        foreach (var section in page.Sections)
        {
            section.IsExpanded = open.Contains(section.Title);
            Sections.Add(section);
        }

        HasAttention = page.Attention.Count > 0;
        WorstStatus = page.Attention.Any(r => r.Status == HealthStatus.Bad) ? HealthStatus.Bad
            : HasAttention ? HealthStatus.Warning
            : HealthStatus.Good;
        AttentionText = HasAttention ? $"{page.Attention.Count} to look at" : "Nothing needs a look";
        FineText = $"{page.Fine} fine";
    }

    /// <summary>Opens the Windows page a row points to. The row is the button's parameter.</summary>
    [RelayCommand]
    private void OpenCheck(HealthRowViewModel? row)
    {
        if (row?.Check.OpenUri is { } uri)
        {
            ShellLinks.Open(uri, _log);
        }
    }

    private static string ProtectionNoteFor((string Vendor, string App)? vendor) =>
        (vendor is var (name, app) ? $"{app}, {name} services, " : string.Empty) +
        "AMD and Intel drivers, Realtek audio, Defender, Windows Update and Game Pass. This app cannot " +
        "change any of them; this only checks that nothing else has.";

    [RelayCommand]
    private async Task RepairAsync()
    {
        var vendor = _systemState.GetVendorSoftware() is var (name, _) ? $"{name}, " : string.Empty;

        var confirmed = await Shell.ConfirmAsync(
            "Repair protected services",
            $"Any {vendor}AMD, Realtek, Defender, Windows Update or Game Pass service currently set to " +
            "Disabled will be set back to Automatic and started.\n\nThis only ever re-enables services; " +
            "it cannot disable anything.",
            "Repair now");

        if (!confirmed)
        {
            return;
        }

        await Shell.RunExclusiveAsync("Repairing protected services…", async (progress, ct) =>
        {
            await _systemState.RepairProtectedServicesAsync(ct);
            await LoadChecksAsync(ct);
        });
    }
}
