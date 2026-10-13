using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

public enum UpdateRowState
{
    Available,
    Working,
    Done,
    Failed
}

public sealed partial class UpdateRowViewModel(UpdateItem item, UpdatesViewModel page) : ObservableObject
{
    public UpdateItem Item { get; } = item;

    public string Name => Item.Name;

    public string VersionText => Item switch
    {
        { InstalledVersion: { } from, AvailableVersion: { } to } => $"{from} → {to}",
        { InstalledVersion: { } installed } => installed,
        _ => string.Empty
    };

    /// <summary>The winget ID, or the KB number and size of a Windows update. Store apps have nothing more worth showing.</summary>
    public string Detail => Item.Source switch
    {
        UpdateSource.Winget => Item.Key,
        UpdateSource.WindowsUpdate => Item.Detail ?? string.Empty,
        _ => string.Empty
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private UpdateRowState _state = UpdateRowState.Available;

    [ObservableProperty]
    private string _statusText = "Update available";

    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>A failed update can be ticked and tried again; one that is running or done cannot.</summary>
    public bool CanSelect => State is UpdateRowState.Available or UpdateRowState.Failed;

    [RelayCommand]
    private void Skip() => page.Skip(this);
}

/// <summary>One source's list on the page: Windows Update, the Microsoft Store, or apps updated through winget.</summary>
public sealed partial class UpdateSectionViewModel(
    UpdateSource source, string title, string description, string? openText, string? openUri, LogService log)
    : ObservableObject
{
    public UpdateSource Source { get; } = source;
    public string Title { get; } = title;
    public string Description { get; } = description;

    /// <summary>The button that opens this source's own updates page, for anything this page cannot do.</summary>
    public string OpenText { get; } = openText ?? string.Empty;

    public ObservableCollection<UpdateRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not checked yet.";

    [ObservableProperty]
    private string _errorText = string.Empty;

    [RelayCommand]
    private void Open()
    {
        if (openUri is not null)
        {
            ShellLinks.Open(openUri, log);
        }
    }

    /// <summary>Said in place of the count while this source is being asked. Its rows stay as they were.</summary>
    public void ShowChecking()
    {
        ErrorText = string.Empty;
        SummaryText = "Checking…";
    }

    public void RefreshSummary()
    {
        if (ErrorText.Length > 0)
        {
            SummaryText = string.Empty;
            return;
        }

        var waiting = Rows.Count(r => r.State != UpdateRowState.Done);
        SummaryText = waiting switch
        {
            0 when Rows.Count > 0 => "All updated.",
            0 => "Up to date.",
            1 => "1 update.",
            _ => $"{waiting} updates."
        };
    }
}

public sealed partial class SkippedUpdateViewModel(SkippedUpdate entry, UpdatesViewModel page) : ObservableObject
{
    public SkippedUpdate Entry { get; } = entry;
    public string Name => Entry.Name;

    [RelayCommand]
    private void Unskip() => page.Unskip(this);
}

/// <summary>
/// One place to update everything: Windows Update, Microsoft Store apps, and desktop apps winget knows
/// about. Checking asks all three at once, in the background, and shows each list as its source
/// answers: winget takes a few seconds and the Store about twenty. Updating runs them one after
/// another, apps first and Windows Update last, since that is the one most likely to want a restart.
/// </summary>
public sealed partial class UpdatesViewModel : SectionViewModel
{
    private readonly WindowsUpdates _windows;
    private readonly StoreUpdates _store;
    private readonly WingetUpdates _winget;
    private readonly UpdateSettings _settings;
    private readonly RestorePointService _restorePoints;
    private readonly LogService _log;
    private bool _hasScanned;

    /// <summary>True while the sources are being asked. Nothing can be installed until they have answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isChecking;

    /// <summary>How many updates are waiting, once the sources have answered.</summary>
    public override CardStatus? Status => IsChecking || !_hasScanned
        ? CardStatus.Checking
        : CardStatus.ForUpdates(AllRows.Count(r => r.State != UpdateRowState.Done));

    public override bool LoadsFromInternet => true;

    public UpdateSectionViewModel WindowsSection { get; }
    public UpdateSectionViewModel StoreSection { get; }
    public UpdateSectionViewModel AppsSection { get; }
    public IReadOnlyList<UpdateSectionViewModel> Sections { get; }

    public ObservableCollection<SkippedUpdateViewModel> Skipped { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not checked yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private int _selectedCount;

    public string UpdateButtonText => SelectedCount == 0 ? "Update selected" : $"Update selected ({SelectedCount})";

    [ObservableProperty]
    private bool _includeDrivers;

    public bool HasSkipped => Skipped.Count > 0;

    public string SkippedHeader => $"Skipped updates ({Skipped.Count})";

    public UpdatesViewModel(UpdateSources sources, RestorePointService restorePoints, LogService log, IShell shell)
        : base(shell)
    {
        _windows = sources.Windows;
        _store = sources.Store;
        _winget = sources.Winget;
        _settings = sources.Settings;
        _restorePoints = restorePoints;
        _log = log;

        WindowsSection = new UpdateSectionViewModel(
            UpdateSource.WindowsUpdate,
            "Windows Update",
            "Security and quality updates for Windows, the same ones Settings offers.",
            "Open Windows Update",
            "ms-settings:windowsupdate",
            log);

        StoreSection = new UpdateSectionViewModel(
            UpdateSource.Store,
            "Microsoft Store apps",
            "Apps from the Microsoft Store, such as Xbox and Game Bar. The Store downloads and installs them.",
            "Open the Store",
            "ms-windows-store://downloadsandupdates",
            log);

        AppsSection = new UpdateSectionViewModel(
            UpdateSource.Winget,
            "Other apps",
            "Desktop apps winget recognises, such as Steam, Discord and browsers. Each installer is checked against winget's records before it runs.",
            null,
            null,
            log);

        Sections = [WindowsSection, StoreSection, AppsSection];

        _includeDrivers = _settings.LoadIncludeDrivers();

        foreach (var entry in _settings.LoadSkipped())
        {
            Skipped.Add(new SkippedUpdateViewModel(entry, this));
        }

        Skipped.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSkipped));
            OnPropertyChanged(nameof(SkippedHeader));
        };
    }

    private IEnumerable<UpdateRowViewModel> AllRows => Sections.SelectMany(s => s.Rows);

    public override Task LoadAsync() => _hasScanned ? Task.CompletedTask : CheckAsync();

    public override Task RefreshAsync() => CheckAsync();

    partial void OnIncludeDriversChanged(bool value)
    {
        _settings.SaveIncludeDrivers(value);

        if (_hasScanned)
        {
            WindowsSection.SummaryText = "Check again to update this list.";
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        // Not while updates are installing: a list refilled under a running install loses its rows.
        if (IsChecking || Shell.IsBusy)
        {
            return;
        }

        IsChecking = true;
        SummaryText = "Checking…";

        foreach (var section in Sections)
        {
            section.ShowChecking();
        }

        try
        {
            await Shell.RunScanAsync("updates", CheckSourcesAsync);
        }
        finally
        {
            IsChecking = false;

            // A source that never answered (the check itself failed) must not go on saying "Checking…".
            foreach (var section in Sections.Where(s => s.SummaryText == "Checking…"))
            {
                section.RefreshSummary();
            }

            if (SummaryText == "Checking…")
            {
                SummaryText = "Could not check. The console has the detail.";
            }
        }
    }

    private async Task CheckSourcesAsync(CancellationToken ct)
    {
        _log.Info("=== Checking for updates ===");

        async Task<UpdateScan> Run(string name, Func<Task<UpdateScan>> scan)
        {
            try
            {
                return await scan();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"{name} check failed: {ex.Message}");
                return UpdateScan.Failed($"Could not check: {ex.Message}");
            }
        }

        // winget also tracks some Store apps under its own IDs. Updating those through the Store is the
        // way their publisher intends, so they are only listed once, under the Store. The Store answers
        // last, so the winget list is shown as it comes and those apps are taken out of it afterwards.
        HashSet<string>? storeNames = null;

        async Task CheckWindowsAsync()
        {
            var found = await Run("Windows Update", () => _windows.ScanAsync(IncludeDrivers, ct));
            Fill(WindowsSection, found.Items, found.Error);
        }

        async Task CheckStoreAsync()
        {
            var found = await Run("Microsoft Store", () => _store.ScanAsync(ct));
            Fill(StoreSection, found.Items, found.Error);

            storeNames = new HashSet<string>(found.Items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            Remove(AppsSection, row => storeNames.Contains(row.Name));
        }

        async Task CheckAppsAsync()
        {
            var found = await Run("winget", () => _winget.ScanAsync(ct));
            Fill(AppsSection, found.Items.Where(i => storeNames?.Contains(i.Name) != true).ToList(), found.Error);
        }

        await Task.WhenAll(CheckWindowsAsync(), CheckStoreAsync(), CheckAppsAsync());

        _hasScanned = true;
        var total = AllRows.Count();
        SummaryText = total == 0
            ? $"Everything is up to date. Checked at {DateTime.Now:t}."
            : $"{total} update(s) available. Checked at {DateTime.Now:t}.";
    }

    /// <summary>Takes rows out of a section that is already showing, keeping what is ticked on the rest.</summary>
    private void Remove(UpdateSectionViewModel section, Func<UpdateRowViewModel, bool> which)
    {
        foreach (var row in section.Rows.Where(which).ToList())
        {
            row.PropertyChanged -= OnRowChanged;
            section.Rows.Remove(row);
        }

        // Left alone while the section is still waiting for its own answer.
        if (section.SummaryText != "Checking…")
        {
            section.RefreshSummary();
        }

        CountSelected();
    }

    private void Fill(UpdateSectionViewModel section, IReadOnlyList<UpdateItem> items, string? error)
    {
        foreach (var row in section.Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        section.Rows.Clear();
        section.ErrorText = error ?? string.Empty;

        var skipped = Skipped.Select(s => s.Entry.SkipKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(i => !skipped.Contains(i.SkipKey)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new UpdateRowViewModel(item, this);
            row.PropertyChanged += OnRowChanged;
            section.Rows.Add(row);
        }

        section.RefreshSummary();
        CountSelected();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateRowViewModel.IsSelected) or nameof(UpdateRowViewModel.State))
        {
            CountSelected();
        }
    }

    /// <summary>Called whenever a row is added, taken away, ticked or changes state.</summary>
    private void CountSelected()
    {
        SelectedCount = AllRows.Count(r => r.IsSelected && r.CanSelect);
        OnPropertyChanged(nameof(Status));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in AllRows.Where(r => r.CanSelect))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in AllRows)
        {
            row.IsSelected = false;
        }
    }

    internal void Skip(UpdateRowViewModel row)
    {
        if (Shell.IsBusy)
        {
            return;
        }

        var section = Sections.First(s => s.Source == row.Item.Source);
        row.PropertyChanged -= OnRowChanged;
        section.Rows.Remove(row);
        section.RefreshSummary();
        CountSelected();

        Skipped.Add(new SkippedUpdateViewModel(new SkippedUpdate(row.Item.SkipKey, row.Item.Name), this));
        _settings.SaveSkipped(Skipped.Select(s => s.Entry));
        _log.Info($"Skipped {row.Item.Name}. It will not be listed again until it is unskipped.");
    }

    internal void Unskip(SkippedUpdateViewModel entry)
    {
        if (Shell.IsBusy)
        {
            return;
        }

        Skipped.Remove(entry);
        _settings.SaveSkipped(Skipped.Select(s => s.Entry));
        _log.Info($"Unskipped {entry.Name}. It will be listed the next time updates are checked.");
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        if (IsChecking)
        {
            await Shell.ConfirmAsync("Still checking", "Wait for the check to finish, then choose what to install.", "OK");
            return;
        }

        var rows = AllRows.Where(r => r.IsSelected && r.CanSelect).ToList();

        if (rows.Count == 0)
        {
            await Shell.ConfirmAsync(
                "Nothing selected",
                _hasScanned ? "Tick the updates to install first." : "Check for updates first.",
                "OK");
            return;
        }

        var windows = rows.Where(r => r.Item.Source == UpdateSource.WindowsUpdate).ToList();
        var store = rows.Where(r => r.Item.Source == UpdateSource.Store).ToList();
        var apps = rows.Where(r => r.Item.Source == UpdateSource.Winget).ToList();

        var lines = new List<string>();
        if (apps.Count > 0) lines.Add($"  • {apps.Count} app(s) through winget");
        if (store.Count > 0) lines.Add($"  • {store.Count} Microsoft Store app(s)");
        if (windows.Count > 0) lines.Add($"  • {windows.Count} Windows update(s)");

        var message =
            $"{rows.Count} update(s) will be installed:\n\n{string.Join("\n", lines)}\n\n" +
            "Close any apps being updated first, and keep the handheld plugged in." +
            (apps.Count > 0 ? " A System Restore point is made before the winget apps." : string.Empty);

        if (!await Shell.ConfirmAsync("Install updates?", message, "Update"))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Installing updates", async (status, ct) =>
        {
            var total = rows.Count;
            var finished = 0;
            var results = new List<UpdateOutcome>();

            // Made here, on the UI thread, so reports from installer output threads land back on it.
            var progress = new Progress<UpdateProgress>(p =>
            {
                var row = rows.FirstOrDefault(r => ReferenceEquals(r.Item, p.Item));
                if (row is null)
                {
                    return;
                }

                row.StatusText = p.Step;

                if (p.Outcome is null)
                {
                    row.State = UpdateRowState.Working;
                    status.Report($"{finished + 1} of {total}: {row.Name}. {p.Step}");
                    return;
                }

                finished++;
                results.Add(p.Outcome);
                row.State = p.Outcome.Kind == UpdateOutcomeKind.Failed ? UpdateRowState.Failed : UpdateRowState.Done;
                if (row.State == UpdateRowState.Done)
                {
                    row.IsSelected = false;
                }
            });

            foreach (var row in rows)
            {
                row.State = UpdateRowState.Working;
                row.StatusText = "Waiting…";
            }

            _log.Info($"=== Installing {total} update(s) ===");

            try
            {
                if (apps.Count > 0)
                {
                    status.Report("Creating System Restore point…");
                    var rp = await _restorePoints.CreateAsync("Handheld Optimiser: app updates", ct);

                    if (!rp.Created)
                    {
                        _log.Error("ABORTED before installing anything: no restore point.");
                        foreach (var row in rows)
                        {
                            row.State = UpdateRowState.Available;
                            row.StatusText = "Update available";
                        }

                        await Shell.ConfirmAsync(
                            "Nothing was installed",
                            $"No System Restore point could be created ({rp.Message}). Nothing was installed.",
                            "OK");
                        return;
                    }

                    await _winget.InstallAsync(apps.Select(r => r.Item).ToList(), progress, ct);
                }

                if (store.Count > 0)
                {
                    await _store.InstallAsync(store.Select(r => r.Item).ToList(), progress, ct);
                }

                if (windows.Count > 0)
                {
                    await _windows.InstallAsync(windows.Select(r => r.Item).ToList(), progress, ct);
                }

                // Progress reports are posted to this thread, so give the last ones a turn to arrive.
                await Task.Yield();
            }
            finally
            {
                // Anything left mid-way (an exception, a cancel) goes back to being an available update.
                foreach (var row in rows.Where(r => r.State == UpdateRowState.Working))
                {
                    row.State = UpdateRowState.Failed;
                    row.StatusText = "Stopped before it finished.";
                }

                foreach (var section in Sections)
                {
                    section.RefreshSummary();
                }

                CountSelected();
            }

            var updated = results.Count(r => r.Kind == UpdateOutcomeKind.Updated);
            var current = results.Count(r => r.Kind == UpdateOutcomeKind.AlreadyCurrent);
            var failed = rows.Count(r => r.State == UpdateRowState.Failed);
            var reboot = results.Any(r => r.RebootRequired);

            if (reboot)
            {
                Shell.NotifyRebootRequired();
            }

            _log.Info($"=== Updates finished: {updated} updated, {current} already current, {failed} failed ===");

            await Shell.ConfirmAsync(
                "Updates finished",
                $"{updated} updated.\n" +
                (current > 0 ? $"{current} were already up to date.\n" : string.Empty) +
                (failed > 0 ? $"{failed} failed. Each one says why on its row, and the console has the detail.\n" : string.Empty) +
                (reboot ? "\nRestart to finish installing." : string.Empty),
                "OK");
        });
    }
}
