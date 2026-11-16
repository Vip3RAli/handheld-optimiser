using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Where each tile of the grid goes: as many columns as fit, spread so the first and last are on the
/// edges and what is left over goes between them. A WrapPanel left it all at the right, which the user
/// saw as the grid not using the whole screen (2026-11-16).
/// </summary>
internal static class FullWidthGrid
{
    /// <summary>How many tiles of a width fit across, never fewer than one.</summary>
    public static int Columns(double width, double tileWidth) =>
        tileWidth <= 0 || double.IsInfinity(width) ? 1 : Math.Max(1, (int)Math.Floor((width + 0.001) / tileWidth));

    /// <summary>The room added between two columns, so the last ends on the right edge.</summary>
    public static double Gap(double width, double tileWidth, int columns) =>
        columns < 2 || double.IsInfinity(width) ? 0 : Math.Max(0, (width - columns * tileWidth) / (columns - 1));
}

/// <summary>The grid layout's panel: rows of tiles that are all the same size, across the whole width.</summary>
internal sealed class FullWidthGridPanel : Panel
{
    private Size _tile;

    protected override Size MeasureOverride(Size availableSize)
    {
        _tile = default;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _tile = new Size(Math.Max(_tile.Width, child.DesiredSize.Width), Math.Max(_tile.Height, child.DesiredSize.Height));
        }

        var count = InternalChildren.Count;
        var columns = FullWidthGrid.Columns(availableSize.Width, _tile.Width);
        var rows = (count + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? Math.Min(count, columns) * _tile.Width : availableSize.Width;
        return new Size(count == 0 ? 0 : width, rows * _tile.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = FullWidthGrid.Columns(finalSize.Width, _tile.Width);
        var gap = FullWidthGrid.Gap(finalSize.Width, _tile.Width, columns);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var x = i % columns * (_tile.Width + gap);
            var y = i / columns * _tile.Height;
            InternalChildren[i].Arrange(new Rect(new Point(x, y), _tile));
        }

        return finalSize;
    }
}
