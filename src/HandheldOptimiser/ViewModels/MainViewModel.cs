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

    /// <summary>Everything the window can show: the Dashboard, then one page per goal in the map's order.</summary>
    public ObservableCollection<PageViewModelBase> Pages { get; } = [];

    /// <summary>The goals' pages, which the Dashboard shows as a card each.</summary>
    public IReadOnlyList<GoalPageViewModel> Cards { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    private PageViewModelBase? _selectedPage;

    /// <summary>Whether the Dashboard is on screen. Every other page has the header, with Back, above it.</summary>
    public bool IsHome => SelectedPage is DashboardViewModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText), nameof(IsWorking))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string _statusText = "Ready";

    // What is being read in the background right now, in the order it started.
    private readonly List<string> _scans = [];

    /// <summary>"Checking tweaks, updates…" while background scans run, and empty when none does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText), nameof(IsWorking))]
    private string _scanText = string.Empty;

    /// <summary>The line at the bottom of the window: the running operation, else what is being checked.</summary>
    public string FooterText => IsBusy || ScanText.Length == 0 ? StatusText : ScanText;

    /// <summary>Whether the ring at the bottom of the window turns: for an operation or a background scan.</summary>
    public bool IsWorking => IsBusy || ScanText.Length > 0;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    private bool _isLogExpanded;

    private readonly UpdateService _updates;
    private readonly TweakStateStore _states;

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
        TweakStateStore states,
        SystemStateService systemState,
        HardwareInfoService hardware,
        RestorePointService restorePoints,
        AppxService appxService,
        StartupService startupService,
        TweakJournalService journal,
        RuntimeService runtimeService,
        UpdateSources updateSources,
        UpdateService updates)
    {
        Log = log;
        _updates = updates;
        _states = states;

        var dashboard = new DashboardViewModel(engine, states, systemState, hardware, restorePoints, this);
        Pages.Add(dashboard);

        // What each kind of section in the map is made of.
        SectionViewModel Build(SectionSpec spec, OpenTile open) => spec.Kind switch
        {
            SectionKind.Tweaks => new TweakSectionViewModel(spec.Category!.Value, engine, states, this, open) { Heading = spec.Heading },
            SectionKind.HomeApp => new HomeAppPickerViewModel(log, this) { Heading = spec.Heading },
            SectionKind.Startup => new StartupViewModel(startupService, this, open) { Heading = spec.Heading },
            SectionKind.PreinstalledApps => new BloatwareViewModel(appxService, restorePoints, journal, log, this, open) { Heading = spec.Heading },
            SectionKind.Backup => new BackupViewModel(engine, states, restorePoints, this) { Heading = spec.Heading },
            SectionKind.Jobs => new PowerActionsViewModel(engine, states, this) { Heading = spec.Heading },
            SectionKind.Health => new DeviceHealthViewModel(systemState, log, this) { Heading = spec.Heading },
            SectionKind.Updates => new UpdatesViewModel(updateSources, restorePoints, log, this) { Heading = spec.Heading },
            SectionKind.Runtimes => new GameRuntimesViewModel(runtimeService, this) { Heading = spec.Heading },
            _ => throw new InvalidOperationException($"No section is built for {spec.Kind}.")
        };

        foreach (var goal in HubLayout.Goals)
        {
            // One per page, so that only one tile is open on a page whichever section it is in.
            var open = new OpenTile();
            Pages.Add(new GoalPageViewModel(goal, [.. goal.Sections.Select(s => Build(s, open))], engine, states, this));
        }

        Cards = [.. Pages.OfType<GoalPageViewModel>()];
        SelectedPage = dashboard;
    }

    /// <summary>A card on the Dashboard was pressed.</summary>
    [RelayCommand]
    private void OpenPage(PageViewModelBase page) => SelectedPage = page;

    /// <summary>Back. There are only two levels, so it always goes to the Dashboard.</summary>
    [RelayCommand]
    private void GoHome() => SelectedPage = Pages[0];

    partial void OnSelectedPageChanged(PageViewModelBase? oldValue, PageViewModelBase? newValue) =>
        oldValue?.OnNavigatedFrom();

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
            // A page opened while something is being applied waits for that to finish before it reads
            // anything, so it does not show a state caught halfway through a change.
            await _gate.WaitAsync();
            _gate.Release();

            // The user may have moved on while we waited; only load the page they are looking at.
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

    public async Task RunScanAsync(string what, Func<CancellationToken, Task> scan)
    {
        _scans.Add(what);
        ShowScans();

        try
        {
            await scan(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Nothing to say: whatever was shown before still is.
        }
        catch (Exception ex)
        {
            Log.Error($"Checking {what} failed: {ex.Message}");
        }
        finally
        {
            _scans.Remove(what);
            ShowScans();
        }
    }

    private void ShowScans() =>
        ScanText = _scans.Count == 0 ? string.Empty : $"Checking {string.Join(", ", _scans.Distinct())}…";

    /// <summary>
    /// Reads everything the app shows, once, as it opens: every tweak's state and each section's own
    /// list. Opening a page used to start its scan and hold the app busy until it finished. What only
    /// reads this device goes first and is done in a few seconds; the sections that ask the internet
    /// (Updates and Game runtimes) follow, so they do not slow the rest down.
    /// </summary>
    public async Task ScanAtLaunchAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sections = Cards.SelectMany(card => card.Sections.Select(section => (card.Title, Section: section))).ToList();

        // Only the ones not asked about yet: the Dashboard is already on screen and has asked for its own.
        var tweaks = RunScanAsync("tweaks", ct => _states.CheckMissingAsync(_states.Tweaks, ct));
        await Task.WhenAll(sections
            .Where(s => !s.Section.LoadsFromInternet)
            .Select(s => LoadQuietlyAsync(s.Title, s.Section.LoadAsync))
            .Append(LoadQuietlyAsync(Pages[0].Title, Pages[0].LoadAsync))
            .Append(tweaks));
        Log.Info($"Start-up scan: this device read in {clock.Elapsed.TotalSeconds:0.0} s.");

        await Task.WhenAll(sections
            .Where(s => s.Section.LoadsFromInternet)
            .Select(s => LoadQuietlyAsync(s.Title, s.Section.LoadAsync)));
        Log.Info($"Start-up scan: updates and runtimes read after {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    /// <summary>One section failing to load must not stop the others, so each reports its own failure.</summary>
    private async Task LoadQuietlyAsync(string page, Func<Task> load)
    {
        try
        {
            await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Failed to load {page}: {ex.Message}");
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            MaxWidth = 460
        };

        // Only a long message, such as every app about to be removed, scrolls inside the dialog, so
        // its buttons stay on a 720 unit tall screen. A short one is plain text, as it always was.
        System.Windows.UIElement content = DialogText.NeedsScrolling(message)
            ? new System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = text
            }
            : text;

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            CloseButtonText = confirmText == "OK" ? "Close" : "Cancel",
            MaxWidth = 560
        };

        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public void NotifyRebootRequired() => RebootRequired = true;

    private bool _closeNoticeShowing;

    /// <summary>
    /// Asked as the window is about to close. True while an operation is running, in which case the
    /// window stays open and says why. Closing mid-run would stop a tweak between its registry writes
    /// and its script, or a removal or update halfway, and what had been changed by then may not have
    /// its undo data saved yet.
    /// </summary>
    public bool ShouldBlockClose()
    {
        if (!IsBusy)
        {
            return false;
        }

        Log.Warning($"Close refused: \"{StatusText}\" is still running.");

        // Pressing close again while the notice is up does not stack a second one behind it.
        if (!_closeNoticeShowing)
        {
            _closeNoticeShowing = true;
            _ = ShowStillWorkingAsync(StatusText);
        }

        return true;
    }

    private async Task ShowStillWorkingAsync(string running)
    {
        try
        {
            // Shown after the close has been turned down, not from inside the window's Closing event.
            await Task.Yield();
            await ConfirmAsync(
                "Still working",
                $"{running} is still running. Closing now could leave a change half made, without what is " +
                "needed to undo it.\n\nThe app can be closed once it has finished.",
                "OK");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not show the still-working notice: {ex.Message}");
        }
        finally
        {
            _closeNoticeShowing = false;
        }
    }

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
    /// <param name="installNow">
    /// True when the game library's Update now opened the app. The update was already chosen there and
    /// the administrator prompt accepted, so it installs without asking again, and a failed check is said.
    /// </param>
    /// <returns>True when the app is closing to install the update, so nothing else should be started.</returns>
    public async Task<bool> CheckForUpdatesOnStartupAsync(bool installNow = false)
    {
        try
        {
            ShowOffer(await _updates.CheckAsync());
        }
        catch (Exception ex) when (IsUpdateCheckFailure(ex))
        {
            Log.Info($"Could not check for updates: {ex.Message}");

            if (installNow)
            {
                await ConfirmAsync("Could not check for updates", $"{ex.Message}\n\nCheck the internet connection and try again.", "OK");
            }

            return false;
        }

        return installNow && AvailableUpdate is { CanInstallInApp: true } offer && await DownloadAndInstallAsync(offer);
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

        await DownloadAndInstallAsync(offer);
    }

    /// <returns>True when the installer was started and the app is closing for it.</returns>
    private async Task<bool> DownloadAndInstallAsync(UpdateOffer offer)
    {
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
            return false;
        }

        if (installer is null)
        {
            return false;
        }

        try
        {
            _updates.LaunchInstaller(installer);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Could not start the update installer: {ex.Message}");
            await ConfirmAsync("Update not installed", $"The installer could not be started ({ex.Message}).", "OK");
            return false;
        }

        // The installer waits for this process to close before replacing its files.
        System.Windows.Application.Current.Shutdown();
        return true;
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
