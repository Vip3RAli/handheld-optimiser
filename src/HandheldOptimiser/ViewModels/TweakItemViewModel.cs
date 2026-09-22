using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One tweak row: a toggle switch, a risk badge, and its live detected state.
/// </summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly TweakEngine _engine;
    private readonly IShell _shell;

    /// <summary>
    /// Guards against the toggle's change handler firing while we are programmatically syncing the
    /// switch to detected state, which would immediately re-apply or revert the tweak.
    /// </summary>
    private bool _suppressToggleHandling;

    public Tweak Tweak { get; }

    [ObservableProperty]
    private TweakState _state = TweakState.Unknown;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isWorking;

    public TweakItemViewModel(Tweak tweak, TweakEngine engine, IShell shell)
    {
        Tweak = tweak;
        _engine = engine;
        _shell = shell;
    }

    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string? Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Tweak.Warning);
    public bool RequiresReboot => Tweak.RequiresReboot;

    public string RiskBadge => Tweak.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Trade-off",
        RiskLevel.SecurityTradeoff => "Security impact",
        RiskLevel.Breaking => "Breaks features",
        _ => string.Empty
    };

    public string StateText => State switch
    {
        TweakState.Applied => "Optimised",
        TweakState.NotApplied => "Default",
        TweakState.Partial => "Partly applied",
        TweakState.Error => "Error",
        _ => "Unknown"
    };

    partial void OnStateChanged(TweakState value)
    {
        OnPropertyChanged(nameof(StateText));

        _suppressToggleHandling = true;
        IsOn = value == TweakState.Applied;
        _suppressToggleHandling = false;
    }

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        State = await _engine.DetectAsync(Tweak, ct);
    }

    /// <summary>
    /// Called by the view when the user flips the switch. Applying runs the full pipeline including a
    /// restore point; reverting uses the journal.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_suppressToggleHandling || _shell.IsBusy)
        {
            return;
        }

        var turningOn = IsOn;

        if (turningOn && Tweak.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking)
        {
            var confirmed = await _shell.ConfirmAsync(
                Tweak.Name,
                $"{Tweak.Description}\n\n{Tweak.Warning}",
                "Apply anyway",
                destructive: true);

            if (!confirmed)
            {
                _suppressToggleHandling = true;
                IsOn = false;
                _suppressToggleHandling = false;
                return;
            }
        }

        await _shell.RunExclusiveAsync(
            turningOn ? $"Applying {Tweak.Name}" : $"Reverting {Tweak.Name}",
            async (progress, ct) =>
            {
                IsWorking = true;

                try
                {
                    if (turningOn)
                    {
                        var summary = await _engine.ApplyAsync([Tweak], Tweak.Name, createRestorePoint: true, progress, ct);

                        if (summary.Aborted)
                        {
                            await _shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                        }

                        if (summary.RebootRequired)
                        {
                            _shell.NotifyRebootRequired();
                        }
                    }
                    else
                    {
                        var result = await _engine.RevertAsync(Tweak, ct);

                        if (result.Status == ResultStatus.Blocked)
                        {
                            await _shell.ConfirmAsync("Cannot revert", result.Message ?? "No undo data.", "OK");
                        }

                        if (result.RebootRequired && !result.IsFailure)
                        {
                            _shell.NotifyRebootRequired();
                        }
                    }

                    await RefreshStateAsync(ct);
                }
                finally
                {
                    IsWorking = false;
                }
            });
    }
}
