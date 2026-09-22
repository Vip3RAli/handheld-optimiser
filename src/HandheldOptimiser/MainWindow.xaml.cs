using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.ViewModels;
using Wpf.Ui.Controls;

namespace HandheldOptimiser;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        DataContextChanged += OnDataContextChanged;
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
