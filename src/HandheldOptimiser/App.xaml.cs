using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using HandheldOptimiser.Services;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser;

public partial class App : Application
{
    private LogService? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        Views.TouchSizing.Apply();
        ApplyBrandAccent();

        if (!IsRunningElevated())
        {
            // The manifest requests administrator, so reaching here means elevation was declined or
            // stripped. Every tweak needs HKLM, DISM or bcdedit, so there is nothing useful to do.
            MessageBox.Show(
                "Handheld Optimiser needs to run as Administrator.\n\n" +
                "Close this and relaunch, accepting the User Account Control prompt.",
                "Administrator required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown(1);
            return;
        }

        var log = new LogService();
        _log = log;
        var runner = new PowerShellRunner(log);
        var registry = new RegistryHelper(log);
        var journal = new TweakJournalService(log);
        var restorePoints = new RestorePointService(log, runner, registry);
        var appxService = new AppxService(log, runner);
        var startupService = new StartupService(log, registry);
        var systemState = new SystemStateService(log, runner, registry);
        var hardware = new HardwareInfoService(log, runner);
        var engine = new TweakEngine(log, registry, runner, restorePoints, journal, systemState.DetectDevice());
        var runtimes = new RuntimeService(log, runner, restorePoints);
        var updates = new UpdateService(log);
        var updateSources = UpdateSources.Create(log, runner);

        log.Info($"Handheld Optimiser {UpdateService.Display(updates.CurrentVersion)} started (elevated).");
        log.Info($"Session log: {log.LogFilePath}");

        HomeAppRegistration.RestoreIfInterrupted(registry, log);
        journal.ImportLegacyFile(id => engine.FindById(id) is not null);
        UpdateService.CleanUpOldDownloads(log);

        // One answer per tweak, shared by every page. Checks run on this thread and wait on PowerShell
        // where they need it, as the pages' own checks did.
        var states = new TweakStateStore(
            engine.AllTweaks,
            (tweak, ct) => engine.DetectAsync(tweak, ct),
            (tweak, ex) => log.Warning($"Could not check \"{tweak.Name}\": {ex.Message}"));

        var viewModel = new MainViewModel(
            log, engine, states, systemState, hardware, restorePoints, appxService, startupService, journal, runtimes,
            updateSources, updates);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // The game library cannot install updates itself, so its Update now starts this app with --update.
        var installUpdate = e.Args.Contains("--update", StringComparer.OrdinalIgnoreCase);
        _ = viewModel.CheckForUpdatesOnStartupAsync(installUpdate);

        // Everything is read once now, so no page has to scan when it is opened. Not when the app was
        // only opened to install an update: it is about to close, and a Store check cut off midway
        // would leave the updates it found paused in the Store's queue. Pages still load when opened.
        if (!installUpdate)
        {
            _ = viewModel.ScanAtLaunchAsync();
        }
    }

    /// <summary>
    /// Pins the accent to the app's cyan instead of the Windows accent, so primary buttons, toggles,
    /// checkboxes, badges and the sidebar highlight look the same on every device. Dark theme fills
    /// use the lighter steps, matching how Fluent derives them from a system accent.
    /// </summary>
    private static void ApplyBrandAccent()
    {
        ApplicationAccentColorManager.Apply(
            systemAccent: Color.FromRgb(0x0E, 0x74, 0x90),
            primaryAccent: Color.FromRgb(0x08, 0x91, 0xB2),
            secondaryAccent: Color.FromRgb(0x06, 0xB6, 0xD4),
            tertiaryAccent: Color.FromRgb(0x22, 0xD3, 0xEE));
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A crash midway through a registry pass is exactly when the user most needs to know where the
        // log is, so surface the path rather than just the exception.
        _log?.Error($"Unhandled exception: {e.Exception}");

        MessageBox.Show(
            $"Something went wrong:\n\n{Innermost(e.Exception).Message}\n\n" +
            "The session log has the detail of what had been executed up to this point.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
