using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using HandheldOptimiser.ViewModels;
using HandheldOptimiser.Views;
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

        // The first card has the keyboard as the app opens, so a D-pad works from the start.
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                PlaceFocus(vm, cameFrom: null);
            }
        };
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
    /// Escape, Alt+Left and the keyboard's Back key go back to the Dashboard, as the Back button does.
    /// Handled as the key bubbles up, not as it tunnels down, so a control that uses Escape for
    /// itself (an open drop-down, say) gets it first.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // With Alt held, the key arrives as a system key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var typing = Keyboard.FocusedElement is TextBoxBase;

        if (!e.Handled && BackKeys.GoesBack(key, Keyboard.Modifiers, typing) && GoHome())
        {
            e.Handled = true;
        }
    }

    /// <summary>The back button on the side of a mouse does the same.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.ChangedButton == MouseButton.XButton1 && GoHome())
        {
            e.Handled = true;
        }
    }

    /// <summary>Goes back to the Dashboard. False when it is already showing, so the press is left alone.</summary>
    private bool GoHome()
    {
        if (DataContext is not MainViewModel { IsHome: false } vm)
        {
            return false;
        }

        vm.GoHomeCommand.Execute(null);
        return true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel vm)
        {
            // A console the user has to manually scroll is useless during a long run.
            vm.Log.Entries.CollectionChanged += OnLogChanged;
            WatchFocus(vm);
        }

        if (e.OldValue is MainViewModel old)
        {
            old.Log.Entries.CollectionChanged -= OnLogChanged;
            UnwatchFocus(old);
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
