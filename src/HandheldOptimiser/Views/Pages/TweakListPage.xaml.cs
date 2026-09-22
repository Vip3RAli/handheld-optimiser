using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views.Pages;

public partial class TweakListPage : UserControl
{
    public TweakListPage() => InitializeComponent();

    /// <summary>
    /// The toggle's two-way binding has already updated IsOn by the time this fires, so the view model
    /// reads the new value to decide whether it is applying or reverting.
    /// </summary>
    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TweakItemViewModel vm })
        {
            vm.ToggleCommand.Execute(null);
        }
    }
}
