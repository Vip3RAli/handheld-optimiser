using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

public sealed partial class StartupRowViewModel(StartupEntry entry, StartupService service, IShell shell)
    : ObservableObject
{
    private readonly StartupService _service = service;
    private readonly IShell _shell = shell;
    private bool _suppress;

    public StartupEntry Entry { get; } = entry;

    [ObservableProperty]
    private bool _isEnabled = entry.IsEnabled;

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
public sealed partial class StartupViewModel(StartupService service, IShell shell) : SectionViewModel(shell)
{
    private readonly StartupService _service = service;


    public ObservableCollection<StartupRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    private bool _hasScanned;

    /// <summary>How many programs start with Windows, once they have been read.</summary>
    public override CardStatus? Status =>
        _hasScanned ? CardStatus.Neutral($"{Entries.Count(e => e.IsEnabled)} at startup") : null;

    public override Task LoadAsync() => _hasScanned ? Task.CompletedTask : ScanAsync();

    public override Task RefreshAsync() => ScanAsync();

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

            SummaryText = $"{found.Count} entries, {found.Count(e => e.IsEnabled)} enabled.";
            _hasScanned = true;
            OnPropertyChanged(nameof(Status));

            return Task.CompletedTask;
        });
    }
}
