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
    public bool IsProtected => Entry.IsProtected;
    public string? ProtectedReason => Entry.ProtectedReason;
    public bool CanToggle => !Entry.IsProtected;

    [RelayCommand]
    private void Toggle()
    {
        if (_suppress || IsProtected || _shell.IsBusy)
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
/// Startup entry manager. ASUS, AMD, Realtek and security entries are listed but locked: visible so the
/// user can confirm they are present and untouched, not editable.
/// </summary>
public sealed partial class StartupViewModel(StartupService service, IShell shell) : PageViewModelBase(shell)
{
    private readonly StartupService _service = service;

    public override string Title => "Startup Apps";
    public override string Glyph => "";
    public override string Subtitle => "Disable startup programs. Hardware and security entries are locked.";

    public ObservableCollection<StartupRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    public override async Task OnNavigatedToAsync()
    {
        if (Entries.Count == 0)
        {
            await ScanAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        await Shell.RunExclusiveAsync("Reading startup entries…", (progress, ct) =>
        {
            var found = _service.Scan();

            Entries.Clear();
            foreach (var entry in found)
            {
                Entries.Add(new StartupRowViewModel(entry, _service, Shell));
            }

            SummaryText = $"{found.Count} entries: {found.Count(e => e.IsProtected)} locked as protected, " +
                          $"{found.Count(e => !e.IsProtected)} you can change.";

            return Task.CompletedTask;
        });
    }
}
