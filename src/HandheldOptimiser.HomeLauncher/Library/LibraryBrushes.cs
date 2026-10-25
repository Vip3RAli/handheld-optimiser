using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Brushes made once and frozen, so any thread can draw with them and none of them is copied.</summary>
internal static class LibraryBrushes
{
    public static Brush Frozen(string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brush;
    }
}
