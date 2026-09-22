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
        DataContextChanged += OnDataContextChanged;
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
