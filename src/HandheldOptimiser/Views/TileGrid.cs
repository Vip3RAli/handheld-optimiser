using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser.Views;

/// <summary>
/// Lays tiles out a fixed number across, every tile in a row as tall as the tallest of them. A cell
/// that is an <see cref="ISpansRow"/> (the open tile's detail) gets a row to itself, the full width.
/// Where each cell goes is worked out by <see cref="TileRows.Place"/>, which is tested without a window.
/// </summary>
public sealed class TileGrid : Panel
{
    /// <summary>The space between tiles, across and down.</summary>
    private const double Gap = 8;

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(TileGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private IReadOnlyList<(int Row, int Column, int Span)> _placed = [];
    private double[] _rowHeights = [];

    private int ColumnCount => Math.Max(1, Columns);

    /// <summary>A cell made by an ItemsControl is a ContentPresenter around the item.</summary>
    private static bool SpansRow(UIElement child) =>
        child is ContentPresenter { Content: ISpansRow } or FrameworkElement { DataContext: ISpansRow };

    private double TileWidth(double width) => Math.Max(0, (width - Gap * (ColumnCount - 1)) / ColumnCount);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Never given all the room in the world in this app: the page only scrolls up and down.
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var tileWidth = TileWidth(width);

        _placed = TileRows.Place([.. InternalChildren.Cast<UIElement>().Select(SpansRow)], ColumnCount);
        _rowHeights = new double[_placed.Count == 0 ? 0 : _placed[^1].Row + 1];

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var (row, _, span) = _placed[i];
            var child = InternalChildren[i];

            child.Measure(new Size(span == 1 ? tileWidth : width, double.PositiveInfinity));
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
        }

        var height = _rowHeights.Sum() + Gap * Math.Max(0, _rowHeights.Length - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tileWidth = TileWidth(finalSize.Width);

        var tops = new double[_rowHeights.Length];
        for (var row = 1; row < tops.Length; row++)
        {
            tops[row] = tops[row - 1] + _rowHeights[row - 1] + Gap;
        }

        for (var i = 0; i < InternalChildren.Count && i < _placed.Count; i++)
        {
            var (row, column, span) = _placed[i];
            var left = column * (tileWidth + Gap);
            var width = span == 1 ? tileWidth : finalSize.Width;

            InternalChildren[i].Arrange(new Rect(left, tops[row], width, _rowHeights[row]));
        }

        return finalSize;
    }
}
