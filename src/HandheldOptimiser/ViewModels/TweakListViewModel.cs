using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>A block of page-specific settings shown above a tweak list.</summary>
public interface IPageSection
{
    void Reload();
}

/// <summary>
/// A page showing every tweak in one or more categories. Backs both the Gaming Tweaks and System Debloat
/// pages, which differ only in which categories they include and their heading text.
/// </summary>
public sealed partial class TweakListViewModel : PageViewModelBase
{
    private readonly TweakEngine _engine;
    private readonly TweakStateStore _states;

    public override string Title { get; }
    public override string Glyph { get; }
    public override string Subtitle { get; }

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];

    /// <summary>
    /// Optional settings shown above the toggles, rendered through its own DataTemplate. Reloaded along
    /// with the tweak states on Re-check.
    /// </summary>
    public IPageSection? Header { get; }

    public TweakListViewModel(
        string title,
        string glyph,
        string subtitle,
        TweakCategory[] categories,
        TweakEngine engine,
        TweakStateStore states,
        IShell shell,
        IPageSection? header = null)
        : base(shell)
    {
        Title = title;
        Glyph = glyph;
        Subtitle = subtitle;
        _engine = engine;
        _states = states;
        Header = header;

        foreach (var tweak in engine.AllTweaks.Where(t => categories.Contains(t.Category)))
        {
            Tweaks.Add(new TweakItemViewModel(tweak, engine, states, shell));
        }
    }

    private List<Tweak> PageTweaks => Tweaks.Select(t => t.Tweak).ToList();

    /// <summary>
    /// Every tweak is checked when the app starts and again after any change, so opening the page
    /// checks nothing. It only asks about tweaks that have never been asked about, which happens when
    /// the scan at start-up did not run.
    /// </summary>
    public override Task LoadAsync() => _states.CheckMissingAsync(PageTweaks);

    public override Task OnNavigatedToAsync()
    {
        Header?.Reload();
        return LoadAsync();
    }

    /// <summary>Re-check: asks about every tweak on the page again, for changes made outside the app.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        Header?.Reload();
        return Shell.RunScanAsync(Title.ToLowerInvariant(), ct => _states.CheckAsync(PageTweaks, ct));
    }

    [RelayCommand]
    private async Task ApplyAllInCategoryAsync()
    {
        var pending = Tweaks.Where(t => t.State != TweakState.Applied && t.IsForThisDevice).Select(t => t.Tweak).ToList();

        // A workaround for another device's fault is never part of a bulk run; it has to be switched on by hand.
        var skipped = Tweaks.Where(t => t.State != TweakState.Applied && !t.IsForThisDevice).ToList();
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

        if (!await Shell.ConfirmAsync($"Apply all {Title}", message, "Create restore point & apply"))
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
            await _states.CheckAllAsync(waitFor: PageTweaks, ct);
        });
    }
}
