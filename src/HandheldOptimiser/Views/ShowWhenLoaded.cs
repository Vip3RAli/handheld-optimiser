using System.Windows;

namespace HandheldOptimiser.Views;

/// <summary>
/// Scrolls an element into view as it appears. A tile's detail opens under the tile's row, and for a
/// tile on the last row on screen that is below the edge of the page: without this, pressing such a
/// tile would seem to do nothing.
/// </summary>
public static class ShowWhenLoaded
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ShowWhenLoaded),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => ((FrameworkElement)sender).BringIntoView();
}
