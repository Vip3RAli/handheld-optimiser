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
        var engine = new TweakEngine(log, registry, runner, restorePoints, journal);
        var runtimes = new RuntimeService(log, runner, restorePoints);

        log.Info("Handheld Optimiser started (elevated).");
        log.Info($"Session log: {log.LogFilePath}");

        HomeAppRegistration.RestoreDeveloperModeIfInterrupted(registry, log);
        journal.ImportLegacyFile(id => engine.FindById(id) is not null);

        var viewModel = new MainViewModel(
            log, engine, systemState, restorePoints, appxService, startupService, journal, runtimes);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Pins the accent to the app's purple instead of the Windows accent, so primary buttons, toggles,
    /// checkboxes, badges and the sidebar highlight look the same on every device. Dark theme fills
    /// use the lighter steps, matching how Fluent derives them from a system accent.
    /// </summary>
    private static void ApplyBrandAccent()
    {
        ApplicationAccentColorManager.Apply(
            systemAccent: Color.FromRgb(0x6D, 0x28, 0xD9),
            primaryAccent: Color.FromRgb(0x7C, 0x3A, 0xED),
            secondaryAccent: Color.FromRgb(0x8B, 0x5C, 0xF6),
            tertiaryAccent: Color.FromRgb(0xA7, 0x8B, 0xFA));
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
