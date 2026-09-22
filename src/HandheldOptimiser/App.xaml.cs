using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using HandheldOptimiser.Services;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

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
        var runner = new PowerShellRunner(log);
        var registry = new RegistryHelper(log);
        var journal = new TweakJournalService(log);
        var restorePoints = new RestorePointService(log, runner, registry);
        var appxService = new AppxService(log, runner);
        var startupService = new StartupService(log, registry);
        var systemState = new SystemStateService(log, runner, registry);
        var engine = new TweakEngine(log, registry, runner, restorePoints, journal);

        log.Info("Handheld Optimiser started (elevated).");
        log.Info($"Session log: {log.LogFilePath}");

        var viewModel = new MainViewModel(
            log, engine, systemState, restorePoints, appxService, startupService, journal);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
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
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
            "The session log has the detail of what had been executed up to this point.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
