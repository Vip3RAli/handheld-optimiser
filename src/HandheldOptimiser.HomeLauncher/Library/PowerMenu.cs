using System.Windows.Controls;
using System.Windows.Interop;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The power menu: sleep, hibernate, restart and shut down, from the power button in the top bar or the
/// controller's View button. Restart and Shut down take a second press, so a stray A cannot end a session.
/// </summary>
internal sealed class PowerMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly ILibraryHost _host;

    public PowerMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _host = host;

        view.SleepItem.Click += (_, _) => Sleep();
        view.HibernateItem.Click += (_, _) => Hibernate();
        view.RestartItem.Click += (_, _) => Confirm(view.RestartItem, "Restart", Power.Restart);
        view.ShutDownItem.Click += (_, _) => Confirm(view.ShutDownItem, "Shut down", Power.ShutDown);

        // Moving off an armed row disarms it, so the second press is always a deliberate one.
        foreach (var row in new[] { view.RestartItem, view.ShutDownItem })
        {
            row.LostKeyboardFocus += (_, _) =>
            {
                if (ReferenceEquals(_menu.Armed, row))
                {
                    _menu.Armed = null;
                    Refresh();
                }
            };
        }
    }

    public void Open()
    {
        if (!_menu.Open(_view.PowerItems, "Power", "This device"))
        {
            return;
        }

        // Asked each time, so switching hibernate on or off in Windows shows here without a restart.
        _view.HibernateItem.Visibility = Power.CanHibernate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        _menu.Armed = null;
        Refresh();
    }

    private void Refresh()
    {
        // The row that has been pressed once says so in place of its name.
        const string confirm = "Press again to confirm";
        _view.RestartItem.Content = ReferenceEquals(_menu.Armed, _view.RestartItem) ? confirm : "Restart";
        _view.ShutDownItem.Content = ReferenceEquals(_menu.Armed, _view.ShutDownItem) ? confirm : "Shut down";
    }

    /// <summary>Sleep, from the menu or the Old School layout's Sleep card.</summary>
    public async void Sleep()
    {
        // Closed first, so the device does not wake up on the power menu.
        _menu.Close();
        Program.Log("Sleep chosen in the power menu");

        // Off the UI thread: the call does not return until the device is awake again.
        var window = new WindowInteropHelper(_host.Window).Handle;
        Report(await Task.Run(() => Power.Sleep(window)));
    }

    /// <summary>Hibernate, from the menu or the Old School layout's Hibernate card.</summary>
    public async void Hibernate()
    {
        _menu.Close();
        Program.Log("Hibernate chosen in the power menu");
        Report(await Task.Run(Power.Hibernate));
    }

    /// <summary>Arms a row on its first press and acts on its second.</summary>
    private void Confirm(Button row, string name, Func<string?> action)
    {
        if (!ReferenceEquals(_menu.Armed, row))
        {
            _menu.Armed = row;
            Refresh();
            return;
        }

        _menu.Close();
        Program.Log($"{name} chosen in the power menu");
        Act(action);
    }

    /// <summary>
    /// Restarts or shuts down, from the menu or an Old School card. A paused game could not close as Windows
    /// asks it to, and would hold up the restart, so it is resumed first.
    /// </summary>
    public void Act(Func<string?> action)
    {
        if (_host.Session is { } session)
        {
            _host.ResumeGame(session);
        }

        Report(action());
    }

    private void Report(string? error)
    {
        if (error is not null)
        {
            Program.Log(error);
            _notices.Show(error);
        }
    }
}
