using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandheldOptimiser.Views;

/// <summary>
/// Makes controls finger-sized, about 44 px (the usual minimum touch target), for the handheld's touch
/// screen. Done by changing each control type's default size rather than restyling it: an app style
/// BasedOn one of WPF-UI's does not pick up its themed template, and the controls fall back to plain
/// Windows looks. A size set on an individual control in XAML still wins.
/// </summary>
public static class TouchSizing
{
    private const double TouchTarget = 44;

    /// <summary>Must run before the first window is created: metadata can only change before use.</summary>
    public static void Apply()
    {
        MinHeight<Wpf.Ui.Controls.Button>(TouchTarget);
        MinHeight<Wpf.Ui.Controls.TextBox>(TouchTarget);
        MinHeight<RadioButton>(TouchTarget);
        MinHeight<CheckBox>(40);
        MinHeight<Expander>(48);

        // The switch is drawn at a fixed size inside its template, so it is scaled up instead.
        var scale = new ScaleTransform(1.3, 1.3);
        scale.Freeze();
        FrameworkElement.LayoutTransformProperty.OverrideMetadata(
            typeof(Wpf.Ui.Controls.ToggleSwitch), new FrameworkPropertyMetadata(scale));
    }

    private static void MinHeight<T>(double value) where T : FrameworkElement =>
        FrameworkElement.MinHeightProperty.OverrideMetadata(typeof(T), new FrameworkPropertyMetadata(value));
}
