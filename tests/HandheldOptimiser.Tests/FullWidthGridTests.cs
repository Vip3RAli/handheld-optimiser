using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class FullWidthGridTests
{
    [Theory]
    [InlineData(1892, 200, 9)]
    [InlineData(2000, 200, 10)]
    [InlineData(150, 200, 1)]
    [InlineData(0, 200, 1)]
    [InlineData(1000, 0, 1)]
    public void Columns_are_as_many_as_fit(double width, double tile, int expected) =>
        Assert.Equal(expected, FullWidthGrid.Columns(width, tile));

    [Fact]
    public void The_last_column_ends_on_the_right_edge()
    {
        var columns = FullWidthGrid.Columns(1892, 200);
        var gap = FullWidthGrid.Gap(1892, 200, columns);
        Assert.Equal(1892, (columns - 1) * (200 + gap) + 200, 6);
    }

    [Fact]
    public void One_column_has_no_gap() => Assert.Equal(0, FullWidthGrid.Gap(150, 200, 1));

    [Fact]
    public void An_unbounded_width_has_no_gap() =>
        Assert.Equal(0, FullWidthGrid.Gap(double.PositiveInfinity, 200, FullWidthGrid.Columns(double.PositiveInfinity, 200)));
}
