using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HandheldOptimiser.ViewModels;
using Wpf.Ui.Controls;

namespace HandheldOptimiser;

public partial class MainWindow : FluentWindow
{
    // The same name is registered by the game library; see MainApp.ShowIfRunning there.
    private const string ShowMessageName = "HandheldOptimiser.ShowMainWindow";
    private const uint MessageFilterAllow = 1;

    private static readonly uint ShowMessage = RegisterWindowMessageW(ShowMessageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Lets the game library bring this window back when the app is already open. The library does not
    /// run as administrator, and Windows stops such a program restoring or messaging this window, so the
    /// one message that asks for it is let through. It carries nothing and only ever shows the window,
    /// which is no more than the taskbar lets any program's user do.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (ShowMessage != 0 && PresentationSource.FromVisual(this) is HwndSource source)
        {
            ChangeWindowMessageFilterEx(source.Handle, ShowMessage, MessageFilterAllow, IntPtr.Zero);
            source.AddHook(OnWindowMessage);
        }
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == ShowMessage)
        {
            // Restore rather than set a state, so a window that was maximised comes back maximised.
            if (WindowState == WindowState.Minimized)
            {
                SystemCommands.RestoreWindow(this);
            }

            Activate();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// A 1080p 7" handheld at 150% scaling has about 720 units of height, less than the default window,
    /// which would push the status bar and console off screen. Shrink to fit and start maximised there.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;

        if (Width <= area.Width && Height <= area.Height)
        {
            return;
        }

        Width = Math.Max(MinWidth, Math.Min(Width, area.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height));
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Pressing the group that is already open closes its list of pages, and pressing it again opens it.
    /// A press on another group selects it instead, which opens its list.
    /// </summary>
    private void OnGroupHeaderPressed(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { TemplatedParent: ListBoxItem { IsSelected: true } } && DataContext is MainViewModel vm)
        {
            vm.IsGroupOpen = !vm.IsGroupOpen;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel vm)
        {
            // A console the user has to manually scroll is useless during a long run.
            vm.Log.Entries.CollectionChanged += OnLogChanged;
        }

        if (e.OldValue is MainViewModel old)
        {
            old.Log.Entries.CollectionChanged -= OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
