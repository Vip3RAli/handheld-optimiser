using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Finding the container an items control has made for one of its items.</summary>
internal static class ItemContainers
{
    /// <summary>
    /// The container for the item at a place, or null when there is no such place, or no container for
    /// it yet. WPF's own lookup answers null for a place past the last item but throws for one before
    /// the first, which is where a step left from the first card lands. Every lookup by place goes
    /// through here, so none of them has to remember that.
    /// </summary>
    public static ContentPresenter? At(ItemsControl items, int index) =>
        index >= 0 && index < items.Items.Count
            ? items.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter
            : null;
}
