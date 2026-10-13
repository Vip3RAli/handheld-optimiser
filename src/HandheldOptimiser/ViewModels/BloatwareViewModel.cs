using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.ViewModels;

public sealed partial class AppxRowViewModel(AppxPresence presence) : ObservableObject, ITile
{
    public AppxPresence Presence { get; } = presence;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether this tile's detail (its package id, where it is installed) is showing under its row.</summary>
    [ObservableProperty]
    private bool _isOpen;

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

    /// <summary>The group's apps as the page draws them, four across, with the open one's detail under its row.</summary>
    public required TileCells Cells { get; init; }
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
    IShell shell,
    OpenTile open)
    : SectionViewModel(shell)
{
    private readonly AppxService _appxService = appxService;
    private readonly RestorePointService _restorePoints = restorePoints;
    private readonly TweakJournalService _journal = journal;
    private readonly LogService _log = log;

    private bool _hasScanned;
    private bool _isScanning;

    public ObservableCollection<AppxGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    /// <summary>How many apps are ticked, said beside the buttons that tick and clear them.</summary>
    [ObservableProperty]
    private string _selectedText = "0 selected";

    private IEnumerable<AppxRowViewModel> AllRows => Groups.SelectMany(g => g.Items);

    public override Task LoadAsync() => _hasScanned ? Task.CompletedTask : ScanAsync();

    public override Task RefreshAsync() => ScanAsync();

    /// <summary>Reads the installed apps in the background. Only removing them holds the app busy.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_isScanning)
        {
            return;
        }

        _isScanning = true;
        SummaryText = "Scanning…";

        try
        {
            await Shell.RunScanAsync("installed apps", LoadPackagesAsync);
        }
        finally
        {
            _isScanning = false;

            // Still saying so means the scan failed, which the console explains.
            if (SummaryText == "Scanning…")
            {
                SummaryText = "The installed apps could not be read. The console has the detail.";
            }
        }
    }

    private async Task LoadPackagesAsync(CancellationToken ct)
    {
        var found = await _appxService.ScanAsync(ct);

        Groups.Clear();

        foreach (var group in found.GroupBy(f => f.Target.Group).OrderBy(g => g.Key))
        {
            ObservableCollection<AppxRowViewModel> items = [.. group.Select(p => new AppxRowViewModel(p))];
            var cells = new TileCells(columns: 4, open);
            cells.Reset(items);

            Groups.Add(new AppxGroupViewModel
            {
                Header = AppxCatalog.GroupHeader(group.Key),
                Items = items,
                Cells = cells
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
            : $"{total} removable app(s) found.";
        SelectedText = $"{selected} selected";
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

        // Every one of them, with its package id: the id is no longer on show on each tile, so this
        // is where nothing gets removed unseen. A long list scrolls inside the dialog.
        var list = string.Join("\n", selected.Select(r => $"  • {r.FriendlyName}  ({r.IdentityName})"));

        var confirmed = await Shell.ConfirmAsync(
            $"Remove {selected.Count} app(s)?",
            $"{list}\n\nThese will be removed for all users and from the provisioned image, so Windows will " +
            "not reinstall them.\n\nThis cannot be undone by the Revert button. Getting one back means " +
            "reinstalling it from the Microsoft Store. A System Restore point will be created first.",
            $"Remove {selected.Count} app(s)");

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
                TweakId = $"{TweakJournalService.RemovalRecordPrefix}{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
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
