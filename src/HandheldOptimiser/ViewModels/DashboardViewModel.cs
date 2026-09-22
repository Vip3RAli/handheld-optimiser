using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The one-click page. "Drag Car Mode" applies every tweak marked for it, after a restore point and after
/// an explicit confirmation listing exactly what carries a warning.
/// </summary>
public sealed partial class DashboardViewModel(
    TweakEngine engine,
    SystemStateService systemState,
    RestorePointService restorePoints,
    LogService log,
    IShell shell)
    : PageViewModelBase(shell)
{
    private readonly TweakEngine _engine = engine;
    private readonly SystemStateService _systemState = systemState;
    private readonly RestorePointService _restorePoints = restorePoints;
    private readonly LogService _log = log;

    public override string Title => "Dashboard";
    public override string Glyph => "";

    [ObservableProperty]
    private string _hardwareModel = "Detecting…";

    [ObservableProperty]
    private bool _isRogAlly;

    [ObservableProperty]
    private string _optimisationStatus = "Not checked yet";

    [ObservableProperty]
    private int _appliedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _protectionEnabled = true;

    [ObservableProperty]
    private bool _canRevert;

    [ObservableProperty]
    private string _revertSummary = string.Empty;

    public override async Task OnNavigatedToAsync() => await RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task RefreshAsync() =>
        await Shell.RunExclusiveAsync("Checking current state…", (progress, ct) => LoadStateAsync(progress, ct));

    private async Task LoadStateAsync(IProgress<string> progress, CancellationToken ct)
    {
        HardwareModel = _systemState.GetHardwareModel();
        IsRogAlly = _systemState.IsRogAlly();

        ProtectionEnabled = await _restorePoints.IsProtectionEnabledAsync(ct);

        var tweaks = _engine.DragCarTweaks.ToList();
        TotalCount = tweaks.Count;

        var applied = 0;
        foreach (var tweak in tweaks)
        {
            progress.Report($"Checking {tweak.Name}");
            if (await _engine.DetectAsync(tweak, ct) == TweakState.Applied)
            {
                applied++;
            }
        }

        AppliedCount = applied;

        OptimisationStatus = applied == 0 ? "Stock Windows: nothing optimised yet"
            : applied == TotalCount ? "Fully optimised"
            : $"Partially optimised: {TotalCount - applied} tweak(s) still available";

        CanRevert = _engine.HasUndoData;
        RevertSummary = CanRevert
            ? "Undo data is available for previously applied tweaks."
            : "No undo data recorded yet.";
    }

    [RelayCommand]
    private async Task RunDragCarAsync()
    {
        var pending = _engine.DragCarTweaks.ToList();

        var risky = pending.Where(t => t.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking).ToList();

        var message =
            $"This applies {pending.Count} tweaks in one pass, after creating a System Restore point.\n\n" +
            "Windows Update, Microsoft Defender, the firewall, Armoury Crate SE, ASUS services, AMD drivers " +
            "and Realtek audio are never touched.\n\n";

        if (risky.Count > 0)
        {
            message += "These carry real trade-offs:\n" +
                       string.Join("\n\n", risky.Select(t => $"  • {t.Name}\n    {t.Warning}")) +
                       "\n\n";
        }

        message += "Each tweak can be individually reverted afterwards from its category page.";

        if (!await Shell.ConfirmAsync("Drag Car Mode", message, "Create restore point & optimise", destructive: true))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Drag Car Mode", async (progress, ct) =>
        {
            var summary = await _engine.ApplyAsync(pending, "Drag Car Mode", createRestorePoint: true, progress, ct);

            if (summary.Aborted)
            {
                await Shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            await LoadStateAsync(progress, ct);

            await Shell.ConfirmAsync(
                "Drag Car Mode complete",
                $"{summary.Applied} tweak(s) applied.\n" +
                $"{summary.AlreadyDone} were already set.\n" +
                (summary.Failures > 0 ? $"{summary.Failures} failed. Check the log.\n" : string.Empty) +
                (summary.RebootRequired ? "\nRestart required for some changes to take effect." : string.Empty) +
                "\n\nBloatware removal is deliberately separate. Review it on the Bloatware page.",
                "OK");
        });
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (!await Shell.ConfirmAsync(
                "Revert everything",
                "Every tweak this app applied will be restored to the value it had beforehand, using the " +
                "recorded undo data.\n\nRemoved bloatware is not restored. That has to come back from the " +
                "Microsoft Store.",
                "Revert all tweaks",
                destructive: true))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Reverting all tweaks…", async (progress, ct) =>
        {
            var summary = await _engine.RevertAllAsync(progress, ct);

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            await LoadStateAsync(progress, ct);

            await Shell.ConfirmAsync(
                "Revert finished",
                $"{summary.Applied} tweak(s) reverted." +
                (summary.Failures > 0 ? $"\n{summary.Failures} failed. Check the log." : string.Empty),
                "OK");
        });
    }

    [RelayCommand]
    private async Task CreateRestorePointOnlyAsync()
    {
        await Shell.RunExclusiveAsync("Creating System Restore point…", async (progress, ct) =>
        {
            var result = await _restorePoints.CreateAsync("Handheld Optimiser: manual checkpoint", ct);

            await Shell.ConfirmAsync(
                result.Created ? "Restore point created" : "Could not create restore point",
                result.Message,
                "OK");

            ProtectionEnabled = await _restorePoints.IsProtectionEnabledAsync(ct);
        });
    }
}
