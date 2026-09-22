using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// Read-only verification page.
///
/// This app is forbidden from disabling anything ASUS, AMD, Realtek, Defender or Windows Update, so this
/// page has no toggles at all. Its job is to show that those components are present and healthy, and to
/// repair them if some other debloat tool got there first.
/// </summary>
public sealed partial class AsusHealthViewModel(SystemStateService systemState, IShell shell)
    : PageViewModelBase(shell)
{
    private readonly SystemStateService _systemState = systemState;

    public override string Title => "ASUS & Health";
    public override string Glyph => "";
    public override string Subtitle => "Verify protected components are untouched. No toggles here by design.";

    public ObservableCollection<HealthCheck> Checks { get; } = [];

    [ObservableProperty]
    private string _hardwareModel = "Detecting…";

    [ObservableProperty]
    private bool _isRogAlly;

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
        IsRogAlly = _systemState.IsRogAlly();

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
            ? $"{bad} problem(s) found — something has disabled a component that should be running."
            : warn > 0
                ? $"All protected components healthy. {warn} performance opportunit{(warn == 1 ? "y" : "ies")} available."
                : "All protected components healthy and fully optimised.";
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        var confirmed = await Shell.ConfirmAsync(
            "Repair protected services",
            "Any ASUS, AMD, Realtek, Defender, Windows Update or Game Pass service currently set to " +
            "Disabled will be set back to Automatic and started.\n\nThis only ever re-enables services — " +
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
