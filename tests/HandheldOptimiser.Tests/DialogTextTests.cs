using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// A confirmation scrolls only when it is long enough to push its buttons off a handheld's screen,
/// such as a list of every app about to be removed. A short one is left exactly as it always was:
/// put in a scrolling area, a tweak's two paragraph warning grew a scroll bar it had no use for.
/// </summary>
public sealed class DialogTextTests
{
    [Fact]
    public void A_tweaks_warning_does_not_scroll() => Assert.False(DialogText.NeedsScrolling(
        "Stops swipes from the screen edges opening Widgets, Notifications or Task View.\n\n" +
        "Open Notifications and Widgets from the taskbar or Armoury Crate instead."));

    [Fact]
    public void A_long_list_of_apps_scrolls() => Assert.True(DialogText.NeedsScrolling(
        string.Join("\n", Enumerable.Range(1, 40).Select(n => $"  • App {n}  (Publisher.App{n})"))));

    [Fact]
    public void A_dozen_lines_do_not_scroll() => Assert.False(DialogText.NeedsScrolling(
        string.Join("\n", Enumerable.Range(1, 12).Select(n => $"Line {n}"))));

    /// <summary>One very long paragraph wraps into many lines on screen, with no line break in it.</summary>
    [Fact]
    public void A_wall_of_text_scrolls() => Assert.True(DialogText.NeedsScrolling(new string('x', 1500)));
}
