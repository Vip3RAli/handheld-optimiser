using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views.Pages;

public partial class BloatwarePage : UserControl
{
    public BloatwarePage() => InitializeComponent();

    /// <summary>Keeps the "N selected" counter in the header live as rows are ticked.</summary>
    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is BloatwareViewModel vm)
        {
            vm.RefreshSummaryCommand.Execute(null);
        }
    }
}
