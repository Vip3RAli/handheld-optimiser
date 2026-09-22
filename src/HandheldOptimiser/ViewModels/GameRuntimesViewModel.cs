using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

public sealed partial class RuntimeRowViewModel(RuntimePackage package, GameRuntimesViewModel page) : ObservableObject
{
    public RuntimePackage Package { get; } = package;

    public string Name => Package.Name;
    public string Description => Package.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanAct), nameof(ActionText), nameof(VersionText))]
    private RuntimeStatus _status = RuntimeStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _installedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _latestVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _note;

    public string StatusText => Status switch
    {
        RuntimeStatus.UpToDate => "Up to date",
        RuntimeStatus.UpdateAvailable => "Update available",
        RuntimeStatus.Missing when Package.InstallIfMissing => "Not installed",
        RuntimeStatus.Missing => "Not installed (only needed if an app asks for it)",
        RuntimeStatus.Error => $"Could not check: {Note}",
        _ => "Not scanned"
    };

    public string VersionText => Status switch
    {
        RuntimeStatus.UpdateAvailable => $"{InstalledVersion} → {LatestVersion}",
        RuntimeStatus.UpToDate => InstalledVersion ?? string.Empty,
        _ => string.Empty
    };

    /// <summary>Missing .NET runtimes can still be installed from their row; they are only kept out of the bulk install.</summary>
    public bool CanAct => Status is RuntimeStatus.UpdateAvailable or RuntimeStatus.Missing;

    public string ActionText => Status == RuntimeStatus.UpdateAvailable ? "Update" : "Install";

    public void Apply(RuntimeScanResult result)
    {
        Status = result.Status;
        InstalledVersion = result.InstalledVersion;
        LatestVersion = result.LatestVersion;
        Note = result.Note;
    }

    [RelayCommand]
    private Task ActAsync() => page.InstallAsync([this], $"{ActionText} {Name}");
}

/// <summary>
/// Scans the shared runtimes games depend on and installs or updates them through winget.
/// </summary>
public sealed partial class GameRuntimesViewModel : PageViewModelBase
{
    private readonly RuntimeService _service;
    private bool _hasScanned;

    public override string Title => "Game Runtimes";
    public override string Glyph => "";
    public override string Subtitle =>
        "Visual C++, DirectX and .NET runtimes that games need. Installs come from Microsoft and vendor " +
        "servers through winget, which checks each installer before running it.";

    public ObservableCollection<RuntimeRowViewModel> EssentialRows { get; } = [];
    public ObservableCollection<RuntimeRowViewModel> LegacyRows { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Not scanned yet.";

    public GameRuntimesViewModel(RuntimeService service, IShell shell) : base(shell)
    {
        _service = service;

        foreach (var package in service.Catalog)
        {
            var row = new RuntimeRowViewModel(package, this);
            (package.Group == RuntimeGroup.Essential ? EssentialRows : LegacyRows).Add(row);
        }
    }

    private IEnumerable<RuntimeRowViewModel> AllRows => EssentialRows.Concat(LegacyRows);

    public override async Task OnNavigatedToAsync()
    {
        if (!_hasScanned)
        {
            await ScanCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        await Shell.RunExclusiveAsync("Checking game runtimes…", async (progress, ct) =>
        {
            await ScanRowsAsync(AllRows.ToList(), progress, ct);
            _hasScanned = true;
        });
    }

    [RelayCommand]
    private Task UpdateAllAsync()
    {
        var rows = AllRows.Where(r => r.Status == RuntimeStatus.UpdateAvailable).ToList();
        return rows.Count == 0
            ? Shell.ConfirmAsync("Nothing to update", "Every installed runtime is already up to date.", "OK")
            : InstallAsync(rows, "Update all runtimes");
    }

    [RelayCommand]
    private Task InstallMissingAsync()
    {
        var rows = EssentialRows
            .Where(r => r.Status == RuntimeStatus.Missing && r.Package.InstallIfMissing)
            .ToList();

        return rows.Count == 0
            ? Shell.ConfirmAsync("Nothing missing", "Every essential runtime is already installed.", "OK")
            : InstallAsync(rows, "Install missing essentials");
    }

    internal async Task InstallAsync(IReadOnlyList<RuntimeRowViewModel> rows, string title)
    {
        var message =
            $"{rows.Count} runtime(s) will be downloaded and installed silently after a System Restore point " +
            "is created:\n\n" +
            string.Join("\n", rows.Select(r => $"  • {r.Name}")) +
            "\n\nClose any running games first. Runtimes can be removed later from Settings > Apps.";

        if (!await Shell.ConfirmAsync(title, message, "Install"))
        {
            return;
        }

        await Shell.RunExclusiveAsync(title, async (progress, ct) =>
        {
            var summary = await _service.InstallAsync(rows.Select(r => r.Package).ToList(), progress, ct);

            if (summary.Aborted)
            {
                await Shell.ConfirmAsync("Nothing was installed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            progress.Report("Re-checking installed versions");
            await ScanRowsAsync(rows, progress, ct);

            await Shell.ConfirmAsync(
                $"{title} finished",
                $"{summary.Applied} installed or updated.\n" +
                (summary.AlreadyDone > 0 ? $"{summary.AlreadyDone} already current.\n" : string.Empty) +
                (summary.Failures > 0 ? $"{summary.Failures} failed. Check the log.\n" : string.Empty) +
                (summary.RebootRequired ? "\nRestart to finish installing." : string.Empty),
                "OK");
        });
    }

    private async Task ScanRowsAsync(IReadOnlyList<RuntimeRowViewModel> rows, IProgress<string> progress, CancellationToken ct)
    {
        var results = await _service.ScanAsync(rows.Select(r => r.Package).ToList(), progress, ct);

        foreach (var result in results)
        {
            rows.First(r => r.Package.Id == result.Package.Id).Apply(result);
        }

        var all = AllRows.ToList();
        var updates = all.Count(r => r.Status == RuntimeStatus.UpdateAvailable);
        var missing = EssentialRows.Count(r => r.Status == RuntimeStatus.Missing && r.Package.InstallIfMissing);
        var current = all.Count(r => r.Status == RuntimeStatus.UpToDate);

        SummaryText = $"{current} up to date, {updates} update(s) available, {missing} essential(s) not installed.";
    }
}
