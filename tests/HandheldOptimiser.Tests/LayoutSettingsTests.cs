using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class LayoutSettingsTests
{
    [Theory]
    [InlineData("Grid", "ContinuePlaying.grid")]
    [InlineData("Row", "ContinuePlaying.row")]
    [InlineData("OldSchool", "ContinuePlaying.oldschool")]
    public void Each_layout_keeps_its_own_value(string layout, string expected) =>
        Assert.Equal(expected, LibrarySettings.ForLayout("ContinuePlaying", Enum.Parse<LibraryLayout>(layout)));

    [Fact]
    public void A_layout_with_its_own_value_uses_it() => Assert.Equal("off", LibrarySettings.PickForLayout("off", "on"));

    [Fact]
    public void A_layout_without_one_keeps_the_shared_value() => Assert.Equal("on", LibrarySettings.PickForLayout(null, "on"));

    [Fact]
    public void Neither_leaves_the_default() => Assert.Null(LibrarySettings.PickForLayout(null, null));
}
