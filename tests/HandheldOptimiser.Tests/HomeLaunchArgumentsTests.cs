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
    [InlineData(new[] { "--library", "--from-button" }, "Library")]
    public void Arguments_choose_what_opens(string[] args, string expected) =>
        Assert.Equal(expected, Program.ActionFor(args).ToString());

    /// <summary>Only TDP Engine's Armoury Crate button asks the library to take the foreground.</summary>
    [Theory]
    [InlineData(new[] { "--library", "--from-button" }, true)]
    [InlineData(new[] { "--library", "--FROM-BUTTON" }, true)]
    [InlineData(new[] { "--library" }, false)]
    [InlineData(new string[] { }, false)]
    public void Only_the_button_takes_the_foreground(string[] args, bool expected) =>
        Assert.Equal(expected, Program.IsFromButton(args));
}
