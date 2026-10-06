using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.Views.Pages;

public partial class DashboardPage : UserControl
{
    /// <summary>
    /// The narrowest the page can be with the three cards side by side before their contents are
    /// squeezed. A handheld's screen clears it; a window dragged small does not, and stacks them.
    /// </summary>
    private const double ThreeAcrossMinWidth = 780;

    public DashboardPage()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        Cards.Columns = e.NewSize.Width >= ThreeAcrossMinWidth ? 3 : 1;
}
