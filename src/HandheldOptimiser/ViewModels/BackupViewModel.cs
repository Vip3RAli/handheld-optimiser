using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The way back: a restore point made by hand, and Undo all changes, which puts every tweak this app
/// applied back to the value it had before.
/// </summary>
public sealed partial class BackupViewModel : SectionViewModel
{
    private readonly TweakEngine _engine;
    private readonly TweakStateStore _states;
    private readonly RestorePointService _restorePoints;

    public BackupViewModel(TweakEngine engine, TweakStateStore states, RestorePointService restorePoints, IShell shell)
        : base(shell)
    {
        _engine = engine;
        _states = states;
        _restorePoints = restorePoints;

        // Undo data appears when a tweak is applied and goes when it is reverted, and every such change
        // ends with the tweaks being checked again. The journal is held in memory, so asking is cheap.
        _states.Changed += _ => ShowUndoState();
        ShowUndoState();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _canRevert;

    [ObservableProperty]
    private string _revertSummary = string.Empty;

    public override CardStatus? Status => CardStatus.Neutral(CanRevert ? "Undo data saved" : "No undo data yet");

    public override void OnShown() => ShowUndoState();

    public override Task RefreshAsync()
    {
        ShowUndoState();
        return Task.CompletedTask;
    }

    private void ShowUndoState()
    {
        CanRevert = _engine.HasUndoData;
        RevertSummary = CanRevert
            ? "Undo data is available for previously applied tweaks."
            : "No undo data recorded yet.";
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (!await Shell.ConfirmAsync(
                "Undo all changes",
                "Every tweak this app applied goes back to the value it had before.\n\n" +
                "Removed preinstalled apps are not restored. They have to come back from the Microsoft Store.",
                "Undo all changes"))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Undoing all changes…", async (progress, ct) =>
        {
            var summary = await _engine.RevertAllAsync(progress, ct);

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            progress.Report("Checking what changed");
            await _states.CheckAllAsync(ct: ct);
            ShowUndoState();

            await Shell.ConfirmAsync(
                "Undo finished",
                Plural.Count(summary.Applied, "tweak") + " undone." +
                (summary.Failures > 0 ? $"\n{summary.Failures} failed. The console has the detail." : string.Empty),
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
        });
    }
}
