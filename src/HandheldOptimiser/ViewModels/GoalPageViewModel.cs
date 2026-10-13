using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One goal's page: its sections one under another, opened from its card on the Dashboard. The page
/// owns what covers all of it, which is Apply all, Re-check and what its card says.
/// </summary>
public sealed partial class GoalPageViewModel : PageViewModelBase
{
    private readonly TweakEngine _engine;
    private readonly TweakStateStore _states;
    private readonly List<Tweak> _recommended;

    public override string Title { get; }
    public override string Glyph { get; }
    public override string Hint { get; }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    /// <summary>Every tweak on the page, section by section.</summary>
    public IReadOnlyList<Tweak> Tweaks { get; }

    /// <summary>Whether the page has anything for Apply all to apply.</summary>
    public bool HasTweaks => Tweaks.Count > 0;

    public GoalPageViewModel(
        GoalSpec goal,
        IReadOnlyList<SectionViewModel> sections,
        TweakEngine engine,
        TweakStateStore states,
        IShell shell)
        : base(shell)
    {
        _engine = engine;
        _states = states;

        Title = goal.Title;
        Glyph = goal.Glyph;
        Hint = goal.Hint;
        Sections = sections;
        Tweaks = TweakItems.Select(t => t.Tweak).ToList();
        _recommended = Tweaks.Where(t => t.IncludeInOneClick && engine.IsForThisDevice(t)).ToList();

        // Pages live as long as the app does, so neither of these is ever unhooked.
        foreach (var section in sections)
        {
            section.PropertyChanged += OnSectionChanged;
        }

        var ids = Tweaks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        _states.Changed += changed =>
        {
            if (ids.Contains(changed.Id))
            {
                OnPropertyChanged(nameof(Status));
            }
        };
    }

    private IEnumerable<TweakItemViewModel> TweakItems =>
        Sections.OfType<TweakSectionViewModel>().SelectMany(s => s.Tweaks);

    /// <summary>
    /// The first section with something to say decides the card. Where that is a section of tweaks,
    /// the card counts every tweak on the page, whichever heading it is under.
    /// </summary>
    public override CardStatus Status
    {
        get
        {
            foreach (var section in Sections)
            {
                if (section is TweakSectionViewModel)
                {
                    return CardStatus.ForTweaks(_states.Summarise(Tweaks), _states.Summarise(_recommended));
                }

                if (section.Status is { } status)
                {
                    return status;
                }
            }

            return CardStatus.Checking;
        }
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SectionViewModel.Status))
        {
            OnPropertyChanged(nameof(Status));
        }
    }

    public override Task LoadAsync() => Task.WhenAll(Sections.Select(s => s.LoadAsync()));

    public override Task OnNavigatedToAsync()
    {
        foreach (var section in Sections)
        {
            section.OnShown();
        }

        return LoadAsync();
    }

    public override void OnNavigatedFrom()
    {
        foreach (var section in Sections)
        {
            section.OnHidden();
        }
    }

    /// <summary>Re-check: reads every section again, for changes made outside the app.</summary>
    [RelayCommand]
    private Task RefreshAsync() => Task.WhenAll(Sections.Select(s => s.RefreshAsync()));

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        var items = TweakItems.ToList();
        var pending = items.Where(t => t.State != TweakState.Applied && t.IsForThisDevice).Select(t => t.Tweak).ToList();

        // A workaround for another device's fault is never part of a bulk run; it has to be switched on by hand.
        var skipped = items.Where(t => t.State != TweakState.Applied && !t.IsForThisDevice).ToList();
        var skippedNote = skipped.Count == 0
            ? string.Empty
            : "\n\nLeft out because they are for another device:\n" +
              string.Join("\n", skipped.Select(t => $"  • {t.Name} ({t.DeviceBadge})"));

        if (pending.Count == 0)
        {
            await Shell.ConfirmAsync("Already optimised", "Every tweak on this page is already applied." + skippedNote, "OK");
            return;
        }

        var risky = pending.Where(t => t.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking).ToList();

        var message = $"{pending.Count} tweak(s) will be applied after a System Restore point is created." + skippedNote;

        if (risky.Count > 0)
        {
            message += "\n\nIncluding these, which carry warnings:\n" +
                       string.Join("\n", risky.Select(t => $"  • {t.Name}"));
        }

        if (!await Shell.ConfirmAsync($"Apply all on {Title}", message, "Create restore point & apply"))
        {
            return;
        }

        await Shell.RunExclusiveAsync($"Applying {Title}", async (progress, ct) =>
        {
            var summary = await _engine.ApplyAsync(pending, Title, createRestorePoint: true, progress, ct);

            if (summary.Aborted)
            {
                await Shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            // Everything is checked again, since one tweak can move another. The dialog only waits for
            // this page's own.
            await _states.CheckAllAsync(waitFor: Tweaks, ct);
        });
    }
}
