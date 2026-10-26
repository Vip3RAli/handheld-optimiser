using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views.Pages;

/// <summary>
/// The GPU memory row. A closed WPF ComboBox changes its choice on Up and Down, and an open one at every
/// step, so on a page walked with a controller that would set the firmware in passing. Here the closed box
/// lets Up and Down move on and opens on Enter, and the choice is sent only once the list closes.
/// </summary>
public partial class GpuMemorySection : UserControl
{
    public GpuMemorySection() => InitializeComponent();

    private void OnKey(object sender, KeyEventArgs e)
    {
        var box = (ComboBox)sender;
        if (box.IsDropDownOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up or Key.Down:
                box.MoveFocus(new TraversalRequest(e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
                e.Handled = true;
                break;
            case Key.Enter or Key.Space:
                box.IsDropDownOpen = true;
                e.Handled = true;
                break;
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (DataContext is GpuMemorySectionViewModel section)
        {
            _ = section.ApplyAsync();
        }
    }
}
