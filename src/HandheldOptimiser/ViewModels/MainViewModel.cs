using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The shell. Owns navigation, the single "one operation at a time" gate, and the log console.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IShell
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LogService Log { get; }

    public ObservableCollection<PageViewModelBase> Pages { get; } = [];

    [ObservableProperty]
    private PageViewModelBase? _selectedPage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    private bool _isLogExpanded;

    public string LogFilePath => Log.LogFilePath;

    public MainViewModel(
        LogService log,
        TweakEngine engine,
        SystemStateService systemState,
        RestorePointService restorePoints,
        AppxService appxService,
        StartupService startupService,
        TweakJournalService journal,
        RuntimeService runtimeService)
    {
        Log = log;

        Pages.Add(new DashboardViewModel(engine, systemState, restorePoints, log, this));

        Pages.Add(new TweakListViewModel(
            "Gaming Tweaks",
            "",
            "Handheld performance changes. These are where the frame rate comes from.",
            [TweakCategory.Gaming],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "System Debloat",
            "",
            "Telemetry, search, assistants and background activity.",
            [TweakCategory.Debloat, TweakCategory.Privacy],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "CPU & Kernel",
            "",
            "Scheduler settings that put the game ahead of background work.",
            [TweakCategory.CpuKernel],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Network",
            "",
            "Latency and memory fixes for the network stack.",
            [TweakCategory.Network],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Deep Services",
            "",
            "Background services a gaming handheld does not need. Security, update and vendor services are protected.",
            [TweakCategory.Services],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Interface",
            "",
            "Desktop responsiveness and touch behaviour. Games are unaffected.",
            [TweakCategory.Interface],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Storage",
            "",
            "Cuts needless NTFS writes to save SSD wear. Only affects files from now on.",
            [TweakCategory.Storage],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Graphics & Scheduling",
            "",
            "How Windows prioritises the game and schedules the GPU.",
            [TweakCategory.Graphics],
            engine,
            this));

        Pages.Add(new TweakListViewModel(
            "Handheld Usability",
            "",
            "Stops pop-ups and background throttling from interrupting a game.",
            [TweakCategory.Usability],
            engine,
            this));

        Pages.Add(new BloatwareViewModel(appxService, restorePoints, journal, log, this));
        Pages.Add(new StartupViewModel(startupService, this));
        Pages.Add(new PowerActionsViewModel(engine, this));
        Pages.Add(new GameRuntimesViewModel(runtimeService, this));
        Pages.Add(new AsusHealthViewModel(systemState, this));

        SelectedPage = Pages[0];
    }

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        if (value is null)
        {
            return;
        }

        // Fire and forget: page loads are self-contained and report their own failures into the log.
        _ = NavigateToAsync(value);
    }

    private async Task NavigateToAsync(PageViewModelBase page)
    {
        try
        {
            // Page loads wait for any running operation instead of being dropped by RunExclusiveAsync,
            // otherwise a page opened during the startup scan never reads its state and shows "Unknown".
            // Everything here runs on the UI thread, so nothing can take the gate between the release
            // and the page's own RunExclusiveAsync call.
            await _gate.WaitAsync();
            _gate.Release();

            // The user may have moved on while we waited; only scan the page they are looking at.
            if (!ReferenceEquals(SelectedPage, page))
            {
                return;
            }

            await page.OnNavigatedToAsync();
        }
        catch (OperationCanceledException)
        {
            // Navigating away mid-scan is not an error.
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load {page.Title}: {ex.Message}");
        }
    }

    public async Task RunExclusiveAsync(string statusText, Func<IProgress<string>, CancellationToken, Task> work)
    {
        // Non-blocking: if something is already running, say so rather than queueing up a second
        // registry pass behind it.
        if (!await _gate.WaitAsync(0))
        {
            Log.Warning($"\"{statusText}\" ignored because another operation is already running.");
            return;
        }

        IsBusy = true;
        StatusText = statusText;
        ProgressDetail = string.Empty;

        var progress = new Progress<string>(detail => ProgressDetail = detail);

        try
        {
            await work(progress, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Log.Warning($"{statusText} was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"{statusText} failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StatusText = "Ready";
            ProgressDetail = string.Empty;
            _gate.Release();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth = 460
            },
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = confirmText == "OK" ? "Close" : "Cancel",
            MaxWidth = 560
        };

        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public void NotifyRebootRequired() => RebootRequired = true;

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void OpenLogFile()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.LogFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the log file: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (!await ConfirmAsync(
                "Restart now?",
                "Windows will restart immediately. Save anything you have open first.",
                "Restart now"))
        {
            return;
        }

        Log.Warning("Restarting Windows at the user's request.");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/r /t 5 /c \"Handheld Optimiser: applying changes\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
}
