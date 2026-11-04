using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>How the list of present monitors is counted, and when that counts as docked.</summary>
public class MonitorsTests
{
    private static ushort[] List(params string[] names) =>
        [.. names.SelectMany(n => n.Select(c => (ushort)c).Append((ushort)0)), 0];

    [Fact]
    public void An_empty_list_has_no_monitors() =>
        Assert.Equal(0, Monitors.CountNames(List()));

    [Fact]
    public void Each_name_counts_once() =>
        Assert.Equal(2, Monitors.CountNames(List(@"\?\DISPLAY#TMX0002#1", @"\?\DISPLAY#PHL0947#2")));

    [Fact]
    public void Zeros_after_the_end_of_the_list_are_not_names() =>
        Assert.Equal(1, Monitors.CountNames([.. List("A"), 0, 0, 0]));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Docked_is_more_than_the_built_in_screen(int present, bool docked) =>
        Assert.Equal(docked, Monitors.IsDocked(present));
}
