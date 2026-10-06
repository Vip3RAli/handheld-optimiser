using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// Read-only verification page.
///
/// This app is forbidden from disabling the handheld vendor's software, the AMD, Intel and Realtek
/// drivers, Defender or Windows Update, so this page has no toggles at all. Its job is to show that those
/// components are present and healthy, and to repair them if some other debloat tool got there first.
/// Which vendor it inspects follows the device: Armoury Crate on a ROG Ally, Legion Space on a Legion Go,
/// MSI Center M on an MSI Claw.
/// </summary>
public sealed partial class DeviceHealthViewModel(SystemStateService systemState, IShell shell)
    : PageViewModelBase(shell)
{
    private readonly SystemStateService _systemState = systemState;

    public override string Title => "Device & System Health";
    public override string Glyph => "";
    public override string Subtitle => "Verify protected components are untouched. No toggles here by design.";

    public ObservableCollection<HealthCheck> Checks { get; } = [];

    [ObservableProperty]
    private string _hardwareModel = "Detecting…";

    [ObservableProperty]
    private string _protectionNote = ProtectionNoteFor(null);

    [ObservableProperty]
    private string _summaryText = "Not checked yet.";

    [ObservableProperty]
    private bool _hasProblems;

    public override async Task OnNavigatedToAsync()
    {
        if (Checks.Count == 0)
        {
            await RefreshCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() =>
        await Shell.RunExclusiveAsync("Checking protected components…", (progress, ct) => LoadChecksAsync(ct));

    private async Task LoadChecksAsync(CancellationToken ct)
    {
        HardwareModel = _systemState.GetHardwareModel();
        ProtectionNote = ProtectionNoteFor(_systemState.GetVendorSoftware());

        var results = await _systemState.RunHealthChecksAsync(ct);

        Checks.Clear();
        foreach (var check in results)
        {
            Checks.Add(check);
        }

        var bad = results.Count(c => c.Status == HealthStatus.Bad);
        var warn = results.Count(c => c.Status == HealthStatus.Warning);

        HasProblems = bad > 0;

        SummaryText = bad > 0
            ? $"{bad} problem(s) found. Something has disabled a component that should be running."
            : warn > 0
                ? $"All protected components healthy. {warn} performance opportunit{(warn == 1 ? "y" : "ies")} available."
                : "All protected components healthy and fully optimised.";
    }

    private static string ProtectionNoteFor((string Vendor, string App)? vendor) =>
        (vendor is var (name, app) ? $"{app}, {name} services, " : string.Empty) +
        "AMD and Intel drivers, Realtek audio, Defender and Windows Update are all on a hard denylist that " +
        "blocks modification at write time. This page verifies they are intact, and can re-enable them if " +
        "another tool disabled them.";

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
