using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// A page showing every tweak in one or more categories. Backs both the Gaming Tweaks and System Debloat
/// pages — they differ only in which categories they include and their heading text.
/// </summary>
public sealed partial class TweakListViewModel : PageViewModelBase
{
    private readonly TweakEngine _engine;
    private readonly TweakCategory[] _categories;

    public override string Title { get; }
    public override string Glyph { get; }
    public override string Subtitle { get; }

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];

    public TweakListViewModel(
        string title,
        string glyph,
        string subtitle,
        TweakCategory[] categories,
        TweakEngine engine,
        IShell shell)
        : base(shell)
    {
        Title = title;
        Glyph = glyph;
        Subtitle = subtitle;
        _categories = categories;
        _engine = engine;

        foreach (var tweak in engine.AllTweaks.Where(t => categories.Contains(t.Category)))
        {
            Tweaks.Add(new TweakItemViewModel(tweak, engine, shell));
        }
    }

    public override async Task OnNavigatedToAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Shell.RunExclusiveAsync($"Checking {Title.ToLowerInvariant()}…", async (progress, ct) =>
        {
            foreach (var item in Tweaks)
            {
                progress.Report($"Checking {item.Name}");
                await item.RefreshStateAsync(ct);
            }
        });
    }

    [RelayCommand]
    private async Task ApplyAllInCategoryAsync()
    {
        var pending = Tweaks.Where(t => t.State != TweakState.Applied).Select(t => t.Tweak).ToList();

        if (pending.Count == 0)
        {
            await Shell.ConfirmAsync("Already optimised", $"Every tweak on this page is already applied.", "OK");
            return;
        }

        var risky = pending.Where(t => t.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking).ToList();

        var message = $"{pending.Count} tweak(s) will be applied after a System Restore point is created.";

        if (risky.Count > 0)
        {
            message += "\n\nIncluding these, which carry warnings:\n" +
                       string.Join("\n", risky.Select(t => $"  • {t.Name}"));
        }

        if (!await Shell.ConfirmAsync($"Apply all {Title}", message, "Create restore point & apply", destructive: risky.Count > 0))
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

            foreach (var item in Tweaks)
            {
                await item.RefreshStateAsync(ct);
            }
        });
    }
}
