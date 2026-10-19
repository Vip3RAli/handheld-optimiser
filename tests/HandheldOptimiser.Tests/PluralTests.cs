using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

public class PluralTests
{
    [Theory]
    [InlineData(0, "0 tweaks")]
    [InlineData(1, "1 tweak")]
    [InlineData(3, "3 tweaks")]
    public void Count_AddsAnSOnlyWhenNotOne(int n, string expected) =>
        Assert.Equal(expected, Plural.Count(n, "tweak"));

    [Fact]
    public void Count_UsesTheGivenPluralForm() =>
        Assert.Equal("2 children", Plural.Count(2, "child", "children"));
}
