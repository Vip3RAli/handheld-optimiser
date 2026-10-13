using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

public sealed partial class StartupRowViewModel(StartupEntry entry, StartupService service, IShell shell)
    : ObservableObject, ITile
{
    private readonly StartupService _service = service;
    private readonly IShell _shell = shell;
    private bool _suppress;

    public StartupEntry Entry { get; } = entry;

    [ObservableProperty]
    private bool _isEnabled = entry.IsEnabled;

    /// <summary>Whether this tile's detail (its scope and its command line) is showing under its row.</summary>
    [ObservableProperty]
    private bool _isOpen;

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string ScopeLabel => Entry.ScopeLabel;

    [RelayCommand]
    private void Toggle()
    {
        if (_suppress || _shell.IsBusy)
        {
            return;
        }

        var snapshot = _service.SetEnabled(Entry, IsEnabled);

        if (snapshot is null)
        {
            _suppress = true;
            IsEnabled = Entry.IsEnabled;
            _suppress = false;
        }
    }
}

/// <summary>
/// Startup entry manager. Every Run-key entry can be switched on or off, including vendor and security
/// ones; the change is a Task Manager flag, so it is always reversible from here or Task Manager.
/// </summary>
public sealed partial class StartupViewModel(StartupService service, IShell shell, OpenTile open) : SectionViewModel(shell)
{
    private readonly StartupService _service = service;

    public ObservableCollection<StartupRowViewModel> Entries { get; } = [];

    /// <summary>The programs as the page draws them, three across, with the open one's detail under its row.</summary>
    public TileCells Cells { get; } = new(columns: 3, open);

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    private bool _hasScanned;

    /// <summary>How many programs start with Windows, once they have been read.</summary>
    public override CardStatus? Status =>
        _hasScanned ? CardStatus.Neutral($"{Entries.Count(e => e.IsEnabled)} at startup") : null;

    public override Task LoadAsync() => _hasScanned ? Task.CompletedTask : ScanAsync();

    public override Task RefreshAsync() => ScanAsync();

    /// <summary>A tile left open would bring the page back scrolled to it. A page opens at its top, with none open.</summary>
    public override void OnHidden() => Cells.Close();

    /// <summary>Reads the startup entries. It only reads, so it does not hold the app busy.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        await Shell.RunScanAsync("startup apps", ct =>
        {
            var found = _service.Scan();

            Entries.Clear();
            foreach (var entry in found)
            {
                var row = new StartupRowViewModel(entry, _service, Shell);

                // A program switched on or off changes the count on the Dashboard's card.
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(StartupRowViewModel.IsEnabled))
                    {
                        OnPropertyChanged(nameof(Status));
                    }
                };

                Entries.Add(row);
            }

            // New tiles each time, so one left open before the scan is closed by this.
            Cells.Reset(Entries);

            SummaryText = found.Count == 0
                ? "Nothing is set to start with Windows."
                : $"{found.Count} entries, {found.Count(e => e.IsEnabled)} enabled.";
            _hasScanned = true;
            OnPropertyChanged(nameof(Status));

            return Task.CompletedTask;
        });
    }
}
