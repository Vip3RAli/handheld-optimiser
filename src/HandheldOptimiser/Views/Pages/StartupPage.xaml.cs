using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views.Pages;

public partial class StartupPage : UserControl
{
    public StartupPage() => InitializeComponent();

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartupRowViewModel vm })
        {
            vm.ToggleCommand.Execute(null);
        }
    }
}
