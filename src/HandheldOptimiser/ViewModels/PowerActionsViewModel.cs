using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

public sealed partial class PowerActionItemViewModel(PowerAction action, TweakEngine engine, IShell shell)
    : ObservableObject
{
    public PowerAction Action { get; } = action;

    public string Name => Action.Name;
    public string Description => Action.Description;
    public string Glyph => Action.Glyph;
    public string? Warning => Action.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Action.Warning);
    public string DurationHint => Action.DurationHint ?? string.Empty;

    /// <summary>Outcome of the last run this session, shown under the button.</summary>
    [ObservableProperty]
    private string _lastResult = string.Empty;

    [RelayCommand]
    private async Task RunAsync()
    {
        var message = Action.Description;

        if (HasWarning)
        {
            message += $"\n\n{Action.Warning}";
        }

        if (Action.CreateRestorePoint)
        {
            message += "\n\nA System Restore point is created first.";
        }

        if (!string.IsNullOrEmpty(Action.DurationHint))
        {
            message += $"\n\nTakes: {Action.DurationHint}.";
        }

        if (!await shell.ConfirmAsync(Action.Name, message, "Run"))
        {
            return;
        }

        await shell.RunExclusiveAsync(Action.Name, async (progress, ct) =>
        {
            var summary = await engine.RunActionAsync(Action, progress, ct);

            if (summary.Aborted)
            {
                LastResult = "Not run: no restore point could be created.";
                await shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            var result = summary.Results.FirstOrDefault();
            LastResult = $"{DateTime.Now:HH:mm}: {result?.Message ?? "Finished."}";

            if (summary.RebootRequired)
            {
                shell.NotifyRebootRequired();
            }

            await shell.ConfirmAsync(
                result?.IsFailure == true ? $"{Action.Name} had problems" : $"{Action.Name} finished",
                (result?.Message ?? "Finished.") +
                (summary.RebootRequired ? "\n\nRestart to finish applying this." : string.Empty),
                "OK");
        });
    }
}

/// <summary>
/// One-shot maintenance jobs. Buttons, not toggles: none of these has an "on" state or anything to undo.
/// </summary>
public sealed class PowerActionsViewModel : PageViewModelBase
{
    public override string Title => "Power Actions";
    public override string Glyph => "";
    public override string Subtitle => "One-click maintenance. Each runs once; there is nothing to switch back off.";

    public IReadOnlyList<PowerActionItemViewModel> Actions { get; }

    public PowerActionsViewModel(TweakEngine engine, IShell shell) : base(shell)
    {
        Actions = engine.PowerActions.Select(a => new PowerActionItemViewModel(a, engine, shell)).ToList();
    }
}
