namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One entry in the sidebar: a handful of related pages, which drop down as a list under it while it is open.
/// Five of these replace a sidebar that listed every page, which was too many rows to hit reliably with
/// a thumb on a seven inch screen.
/// </summary>
public sealed class NavGroup(string title, string glyph, IReadOnlyList<PageViewModelBase> pages)
{
    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons glyph shown in the sidebar.</summary>
    public string Glyph { get; } = glyph;

    public IReadOnlyList<PageViewModelBase> Pages { get; } = pages;

    /// <summary>The page last open in this group, so coming back to the group comes back to it.</summary>
    public PageViewModelBase SelectedPage { get; set; } = pages[0];

    /// <summary>A group with one page, such as the Dashboard, has nothing to drop down.</summary>
    public bool HasTabs => Pages.Count > 1;
}
