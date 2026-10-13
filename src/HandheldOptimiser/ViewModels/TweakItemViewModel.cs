using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One tweak row: a toggle switch, a risk badge, and its state as last found.
/// </summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly TweakEngine _engine;
    private readonly TweakStateStore _states;
    private readonly IShell _shell;

    /// <summary>
    /// Guards against the toggle's change handler firing while we are programmatically syncing the
    /// switch to detected state, which would immediately re-apply or revert the tweak.
    /// </summary>
    private bool _suppressToggleHandling;

    public Tweak Tweak { get; }

    [ObservableProperty]
    private TweakState _state = TweakState.Unknown;

    /// <summary>
    /// False until the tweak's first check answers. The switch cannot be used before then: it would
    /// show "off" for a tweak that may be on, and pressing it would apply or revert on a guess.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isKnown;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isWorking;

    public TweakItemViewModel(Tweak tweak, TweakEngine engine, TweakStateStore states, IShell shell)
    {
        Tweak = tweak;
        _engine = engine;
        _states = states;
        _shell = shell;

        // Rows live as long as the app does, so this is never unhooked.
        _states.Changed += changed =>
        {
            if (changed.Id == Tweak.Id)
            {
                ShowState();
            }
        };

        ShowState();
    }

    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string? Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Tweak.Warning);
    public bool RequiresReboot => Tweak.RequiresReboot;

    /// <summary>False when the tweak works around a fault on one device family and this is another device.</summary>
    public bool IsForThisDevice => _engine.IsForThisDevice(Tweak);

    public bool HasDeviceBadge => !IsForThisDevice;
    public string DeviceBadge => Tweak.OnlyFor is { } device ? $"{device.DisplayName()} only" : string.Empty;

    public string RiskBadge => Tweak.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Trade-off",
        RiskLevel.SecurityTradeoff => "Security impact",
        RiskLevel.Breaking => "Breaks features",
        _ => string.Empty
    };

    public string StateText => !IsKnown ? "Checking…" : State switch
    {
        TweakState.Applied => "Optimised",
        TweakState.NotApplied => "Default",
        TweakState.Partial => "Partly applied",
        TweakState.Error => "Error",
        _ => "Unknown"
    };

    /// <summary>
    /// Shows what the store holds for this tweak. While a later check is out the last state stays up,
    /// so a re-check changes rows in place and does not blank them.
    /// </summary>
    private void ShowState()
    {
        var found = _states.StateOf(Tweak);
        IsKnown = found is not null;
        State = found ?? TweakState.Unknown;
    }

    partial void OnStateChanged(TweakState value)
    {
        OnPropertyChanged(nameof(StateText));
        SyncToggleToState();
    }

    /// <summary>
    /// Puts the switch back in line with the detected state. OnStateChanged alone is not enough: when an
    /// apply is aborted or a revert has no undo data, State does not change, so the switch would keep the
    /// position the user tapped it to while the tweak stays as it was.
    /// </summary>
    private void SyncToggleToState()
    {
        _suppressToggleHandling = true;
        IsOn = State == TweakState.Applied;
        _suppressToggleHandling = false;
    }

    /// <summary>
    /// Called by the view when the user flips the switch. Applying runs the full pipeline including a
    /// restore point; reverting uses the journal.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_suppressToggleHandling)
        {
            return;
        }

        if (_shell.IsBusy || !IsKnown)
        {
            SyncToggleToState();
            return;
        }

        var turningOn = IsOn;

        if (turningOn && (Tweak.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking || !IsForThisDevice))
        {
            string?[] parts =
            [
                Tweak.Description,
                Tweak.Warning,
                IsForThisDevice
                    ? null
                    : $"This works around a {Tweak.OnlyFor?.DisplayName()} fault, and this device is not a " +
                      $"{Tweak.OnlyFor?.DisplayName()}. Only apply it if you know this device has the same problem."
            ];

            var confirmed = await _shell.ConfirmAsync(
                Tweak.Name,
                string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p))),
                "Apply anyway");

            if (!confirmed)
            {
                SyncToggleToState();
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

                    // One tweak can move another (two that share a setting), so all are checked again.
                    // Only this one is waited for; the rest answer in the background.
                    await _states.CheckAllAsync(waitFor: [Tweak], ct);
                }
                finally
                {
                    IsWorking = false;
                }
            });

        // Also covers the run being turned away because another operation had just started.
        SyncToggleToState();
    }
}
