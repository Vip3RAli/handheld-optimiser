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

    private readonly UpdateService _updates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateTitle), nameof(UpdateMessage), nameof(CanInstallUpdate))]
    private UpdateOffer? _availableUpdate;

    [ObservableProperty]
    private bool _isUpdateBannerOpen;

    public string LogFilePath => Log.LogFilePath;

    public string VersionText => $"Version {UpdateService.Display(_updates.CurrentVersion)}";

    public string UpdateTitle => AvailableUpdate is null
        ? string.Empty
        : $"Handheld Optimiser {UpdateService.Display(AvailableUpdate.Version)} is available";

    public string UpdateMessage => AvailableUpdate switch
    {
        null => string.Empty,
        { CanInstallInApp: true } => "It downloads, checks the signature, installs and reopens the app in about a minute.",
        _ => "This release has to be installed by hand. Open the release page to download it."
    };

    public bool CanInstallUpdate => AvailableUpdate?.CanInstallInApp == true;

    public MainViewModel(
        LogService log,
        TweakEngine engine,
        SystemStateService systemState,
        RestorePointService restorePoints,
        AppxService appxService,
        StartupService startupService,
        TweakJournalService journal,
        RuntimeService runtimeService,
        UpdateService updates)
    {
        Log = log;
        _updates = updates;

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

        Pages.Add(new TweakListViewModel(
            "Full Screen Mode",
            "",
            "Choose what the Windows full screen experience opens, and whether to sign straight into it.",
            [TweakCategory.FullScreen],
            engine,
            this,
            new HomeAppPickerViewModel(log)));

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

    /// <summary>
    /// Opened in Notepad by full path, not through the shell. Shell-opening a .log from this elevated
    /// process would start whatever the user's file association names, and that association is in HKCU,
    /// where anything running as the user can change it to run its own program as administrator.
    /// </summary>
    [RelayCommand]
    private void OpenLogFile()
    {
        try
        {
            var notepad = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"),
                UseShellExecute = false
            };
            notepad.ArgumentList.Add(Log.LogFilePath);
            System.Diagnostics.Process.Start(notepad);
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
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = "/r /t 5 /c \"Handheld Optimiser: applying changes\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Runs once after the window opens. Quiet on purpose: being offline or rate-limited by GitHub is
    /// not something to interrupt anyone about, so failures only reach the log.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            ShowOffer(await _updates.CheckAsync());
        }
        catch (Exception ex) when (IsUpdateCheckFailure(ex))
        {
            Log.Info($"Could not check for updates: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateOffer? offer = null;
        string? failure = null;

        await RunExclusiveAsync("Checking for updates…", async (_, ct) =>
        {
            try
            {
                offer = await _updates.CheckAsync(ct);
            }
            catch (Exception ex) when (IsUpdateCheckFailure(ex))
            {
                failure = ex.Message;
            }
        });

        if (failure is not null)
        {
            await ConfirmAsync("Could not check for updates", $"{failure}\n\nCheck the internet connection and try again.", "OK");
            return;
        }

        if (offer is null)
        {
            await ConfirmAsync("You're up to date", $"Handheld Optimiser {UpdateService.Display(_updates.CurrentVersion)} is the latest version.", "OK");
            return;
        }

        ShowOffer(offer);
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { CanInstallInApp: true } offer)
        {
            return;
        }

        if (!await ConfirmAsync(
                $"Update to {UpdateService.Display(offer.Version)}?",
                $"{SummariseNotes(offer.Notes)}\n\nHandheld Optimiser closes, installs the update and reopens by itself. " +
                "Your applied tweaks and undo data are kept.",
                "Update now"))
        {
            return;
        }

        string? installer = null;
        string? failure = null;

        await RunExclusiveAsync($"Updating to {UpdateService.Display(offer.Version)}", async (progress, ct) =>
        {
            try
            {
                installer = await _updates.DownloadAndVerifyAsync(offer, progress, ct);
            }
            catch (UpdateRejectedException ex)
            {
                Log.Error($"Update refused: {ex.Message}");
                failure = $"{ex.Message}\n\nNothing was installed.";
            }
            catch (Exception ex) when (IsUpdateCheckFailure(ex) || ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error($"Update download failed: {ex.Message}");
                failure = $"The download failed ({ex.Message}). Nothing was installed.";
            }
        });

        if (failure is not null)
        {
            await ConfirmAsync("Update not installed", failure, "OK");
            return;
        }

        if (installer is null)
        {
            return;
        }

        try
        {
            _updates.LaunchInstaller(installer);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not start the update installer: {ex.Message}");
            await ConfirmAsync("Update not installed", $"The installer could not be started ({ex.Message}).", "OK");
            return;
        }

        // The installer waits for this process to close before replacing its files.
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Opens the release page through Explorer rather than the shell. Explorer hands the link to the
    /// user's normal, unelevated session, so the browser does not run as administrator, and nothing
    /// in the user's own settings gets to choose what runs elevated.
    /// </summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (AvailableUpdate is null)
        {
            return;
        }

        try
        {
            var explorer = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false
            };
            explorer.ArgumentList.Add(AvailableUpdate.ReleasePage.AbsoluteUri);
            System.Diagnostics.Process.Start(explorer);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not open the release page: {ex.Message}");
        }
    }

    private void ShowOffer(UpdateOffer? offer)
    {
        AvailableUpdate = offer;
        IsUpdateBannerOpen = offer is not null;
    }

    private static bool IsUpdateCheckFailure(Exception ex) =>
        ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException or UpdateRejectedException;

    /// <summary>Release notes are Markdown; the dialog shows plain text, so the markup is dropped and long notes cut short.</summary>
    private static string SummariseNotes(string notes)
    {
        var lines = notes.Replace("\r", string.Empty).Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", string.Empty).Replace("`", string.Empty))
            .Where(l => !l.StartsWith("SHA-256", StringComparison.OrdinalIgnoreCase));

        var text = string.Join('\n', lines).Trim();
        const int limit = 900;
        return text.Length <= limit ? text : text[..limit].TrimEnd() + "…\n\n(See the release page for the full notes.)";
    }
}
