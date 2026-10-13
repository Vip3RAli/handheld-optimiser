using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The tweaks under one heading of a goal's page: every tweak of one category. Applying them all
/// belongs to the page, which may hold several of these.
/// </summary>
public sealed class TweakSectionViewModel : SectionViewModel
{
    private readonly TweakStateStore _states;
    private readonly List<Tweak> _tweaks;

    public IReadOnlyList<TweakItemViewModel> Tweaks { get; }

    /// <summary>The tiles as the page draws them, three across, with the open one's detail under its row.</summary>
    public TileCells Cells { get; }

    public TweakSectionViewModel(
        TweakCategory category,
        TweakEngine engine,
        TweakStateStore states,
        IShell shell,
        OpenTile open)
        : base(shell)
    {
        _states = states;
        _tweaks = engine.AllTweaks.Where(t => t.Category == category).ToList();
        Tweaks = _tweaks.Select(t => new TweakItemViewModel(t, engine, states, shell)).ToList();

        Cells = new TileCells(columns: 3, open);
        Cells.Reset(Tweaks);

        // Sections live as long as the app does, so this is never unhooked.
        var ids = _tweaks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        _states.Changed += changed =>
        {
            if (ids.Contains(changed.Id))
            {
                OnPropertyChanged(nameof(Summary));
            }
        };
    }

    /// <summary>How many of the section's tweaks are on, once every one of them has answered.</summary>
    public override string Summary =>
        _states.Summarise(_tweaks) is { IsComplete: true } found ? $"{found.Applied} of {found.Total} on" : "Checking…";

    /// <summary>
    /// Every tweak is checked when the app starts and again after any change, so opening the page
    /// checks nothing. It only asks about tweaks that have never been asked about, which happens when
    /// the scan at start-up did not run.
    /// </summary>
    public override Task LoadAsync() => _states.CheckMissingAsync(_tweaks);

    /// <summary>Re-check: asks about every tweak in the section again, for changes made outside the app.</summary>
    public override Task RefreshAsync() => Shell.RunScanAsync("tweaks", ct => _states.CheckAsync(_tweaks, ct));
}
