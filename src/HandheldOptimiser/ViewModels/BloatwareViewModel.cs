using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.ViewModels;

public sealed partial class AppxRowViewModel(AppxPresence presence) : ObservableObject
{
    public AppxPresence Presence { get; } = presence;

    [ObservableProperty]
    private bool _isSelected;

    public string FriendlyName => Presence.Target.FriendlyName;
    public string IdentityName => Presence.Target.IdentityName;
    public string? KeepIfNote => Presence.Target.KeepIfNote;
    public bool HasKeepNote => !string.IsNullOrWhiteSpace(Presence.Target.KeepIfNote);
    public bool Recommended => Presence.Target.Recommended;

    public string ScopeText => (Presence.InstalledForUser, Presence.Provisioned) switch
    {
        (true, true) => "Installed + provisioned",
        (true, false) => "Installed for you",
        (false, true) => "Provisioned only (installs for new users)",
        _ => "Not present"
    };
}

public sealed class AppxGroupViewModel
{
    public required string Header { get; init; }
    public required ObservableCollection<AppxRowViewModel> Items { get; init; }
}

/// <summary>
/// The bloatware page. Nothing is ticked on load. The user reviews each package, with its real Appx
/// identity name visible, before anything is removed.
/// </summary>
public sealed partial class BloatwareViewModel(
    AppxService appxService,
    RestorePointService restorePoints,
    TweakJournalService journal,
    LogService log,
    IShell shell)
    : PageViewModelBase(shell)
{
    private readonly AppxService _appxService = appxService;
    private readonly RestorePointService _restorePoints = restorePoints;
    private readonly TweakJournalService _journal = journal;
    private readonly LogService _log = log;

    private bool _hasScanned;

    public override string Title => "Bloatware";
    public override string Glyph => "";
    public override string Subtitle => "Remove preinstalled apps. Nothing is selected until you choose it.";

    public ObservableCollection<AppxGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    private IEnumerable<AppxRowViewModel> AllRows => Groups.SelectMany(g => g.Items);

    public override async Task OnNavigatedToAsync()
    {
        if (!_hasScanned)
        {
            await ScanCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task ScanAsync() =>
        await Shell.RunExclusiveAsync("Scanning installed apps…", (progress, ct) => LoadPackagesAsync(ct));

    private async Task LoadPackagesAsync(CancellationToken ct)
    {
        var found = await _appxService.ScanAsync(ct);

        Groups.Clear();

        foreach (var group in found.GroupBy(f => f.Target.Group).OrderBy(g => g.Key))
        {
            Groups.Add(new AppxGroupViewModel
            {
                Header = AppxCatalog.GroupHeader(group.Key),
                Items = [.. group.Select(p => new AppxRowViewModel(p))]
            });
        }

        _hasScanned = true;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var total = AllRows.Count();
        var selected = AllRows.Count(r => r.IsSelected);

        SummaryText = total == 0
            ? "No catalog bloatware found on this machine. It is already clean."
            : $"{total} removable app(s) found, {selected} selected.";
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var row in AllRows)
        {
            row.IsSelected = row.Recommended;
        }

        UpdateSummary();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in AllRows)
        {
            row.IsSelected = false;
        }

        UpdateSummary();
    }

    [RelayCommand]
    private void RefreshSummary() => UpdateSummary();

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var selected = AllRows.Where(r => r.IsSelected).ToList();

        if (selected.Count == 0)
        {
            await Shell.ConfirmAsync("Nothing selected", "Tick the apps you want removed first.", "OK");
            return;
        }

        var list = string.Join("\n", selected.Take(15).Select(r => $"  • {r.FriendlyName}  ({r.IdentityName})"));
        if (selected.Count > 15)
        {
            list += $"\n  … and {selected.Count - 15} more";
        }

        var confirmed = await Shell.ConfirmAsync(
            $"Remove {selected.Count} app(s)?",
            $"{list}\n\nThese will be removed for all users and from the provisioned image, so Windows will " +
            "not reinstall them.\n\nThis cannot be undone by the Revert button. Getting one back means " +
            "reinstalling it from the Microsoft Store. A System Restore point will be created first.",
            $"Remove {selected.Count} app(s)",
            destructive: true);

        if (!confirmed)
        {
            return;
        }

        await Shell.RunExclusiveAsync("Removing bloatware…", async (progress, ct) =>
        {
            progress.Report("Creating System Restore point…");

            var rp = await _restorePoints.CreateAsync("Handheld Optimiser: bloatware removal", ct);

            if (!rp.Created)
            {
                _log.Error("ABORTED: no restore point could be created. No apps were removed.");
                await Shell.ConfirmAsync(
                    "Nothing was removed",
                    $"No System Restore point could be created ({rp.Message}). No apps were touched.",
                    "OK");
                return;
            }

            var entry = new TweakJournalEntry
            {
                TweakId = $"appx.removal.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                TweakName = "Bloatware removal",
                AppliedAtUtc = DateTimeOffset.UtcNow
            };

            progress.Report("Removing packages…");

            var (removed, failed) = await _appxService.RemoveAsync(
                [.. selected.Select(s => s.Presence.Target)], entry, ct);

            if (entry.CapturedState.Count > 0)
            {
                // Recorded as a manifest of what was removed, not as undo data. Appx removal is
                // one-way. The list is what makes reinstalling possible later.
                _journal.Record(entry);
            }

            await LoadPackagesAsync(ct);

            await Shell.ConfirmAsync(
                "Bloatware removal finished",
                $"{removed} app(s) removed." +
                (failed > 0 ? $"\n{failed} could not be fully removed. See the log for details." : string.Empty),
                "OK");
        });
    }
}
