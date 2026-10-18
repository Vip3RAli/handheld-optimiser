using HandheldOptimiser.HomeLauncher;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Which arguments make the launcher open the library or Game Bar instead of the saved home app.</summary>
public class HomeLaunchArgumentsTests
{
    [Theory]
    [InlineData(new string[] { }, "Home")]
    [InlineData(new[] { "--library" }, "Library")]
    [InlineData(new[] { "--LIBRARY" }, "Library")]
    [InlineData(new[] { "--gamebar" }, "GameBar")]
    [InlineData(new[] { "--other" }, "Home")]
    public void Arguments_choose_what_opens(string[] args, string expected) =>
        Assert.Equal(expected, Program.ActionFor(args).ToString());
}
