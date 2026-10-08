using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The power menu: sleep, hibernate, restart and shut down.</summary>
public partial class LibraryWindow
{
    // The Restart or Shut down row that has been pressed once and acts on the next press.
    private Button? _confirming;

    /// <summary>The power menu, from the power button in the top bar or the controller's View button.</summary>
    private void OpenPower()
    {
        _menuTile = null;
        _menu = PowerItems;
        HideMenuLists();

        MenuTitle.Text = "Power";
        MenuStore.Text = "This device";

        // Asked each time, so switching hibernate on or off in Windows shows here without a restart.
        HibernateItem.Visibility = Power.CanHibernate ? Visibility.Visible : Visibility.Collapsed;
        _confirming = null;
        RefreshPower();

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    private void RefreshPower()
    {
        const string confirm = "Press again to confirm";
        SleepItem.Tag = _session is { StartedAt: not null } session && LibrarySettings.ResumeAfterSleep
            ? $"Pause here and wake back in {session.Game.Title}"
            : "Pause here and wake where you left off";
        RestartItem.Tag = ReferenceEquals(_confirming, RestartItem) ? confirm : "Close everything and start Windows again";
        ShutDownItem.Tag = ReferenceEquals(_confirming, ShutDownItem) ? confirm : "Close everything and power off";
    }

    private void OnOpenPower(object sender, RoutedEventArgs e) => OpenPower();

    private async void OnSleep(object sender, RoutedEventArgs e)
    {
        // Closed first, so the device does not wake up on the power menu.
        CloseMenu();
        Program.Log("Sleep chosen in the power menu");

        // Off the UI thread: the call does not return until the device is awake again.
        var window = new WindowInteropHelper(this).Handle;
        ReportPower(await Task.Run(() => Power.Sleep(window)));
    }

    private async void OnHibernate(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Program.Log("Hibernate chosen in the power menu");
        ReportPower(await Task.Run(Power.Hibernate));
    }

    private void OnRestart(object sender, RoutedEventArgs e) => ConfirmPower(RestartItem, "Restart", Power.Restart);

    private void OnShutDown(object sender, RoutedEventArgs e) => ConfirmPower(ShutDownItem, "Shut down", Power.ShutDown);

    /// <summary>Arms a row on its first press and acts on its second.</summary>
    private void ConfirmPower(Button row, string name, Func<string?> action)
    {
        if (!ReferenceEquals(_confirming, row))
        {
            _confirming = row;
            RefreshPower();
            return;
        }

        CloseMenu();
        Program.Log($"{name} chosen in the power menu");
        ReportPower(action());
    }

    /// <summary>Moving off an armed row disarms it, so the second press is always a deliberate one.</summary>
    private void OnPowerRowLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(_confirming, sender))
        {
            _confirming = null;
            RefreshPower();
        }
    }

    private void ReportPower(string? error)
    {
        if (error is not null)
        {
            Program.Log(error);
            StatusText.Text = error;
        }
    }
}
