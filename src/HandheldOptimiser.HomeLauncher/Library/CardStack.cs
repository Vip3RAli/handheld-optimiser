using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One stack of Old School cards: the games, the deals, or the green cards of a category.</summary>
internal sealed class CardStack(ItemsControl items)
{
    public ItemsControl Items { get; } = items;

    /// <summary>The place of the card at the front, or on its way there.</summary>
    public int Front { get; set; } = -1;

    /// <summary>A card that a tap has just brought forward, whose click must not press it as well.</summary>
    public object? BroughtForward { get; set; }

    /// <summary>Set when the cards should slide in rather than move from where they were.</summary>
    public bool Fresh { get; set; }

    /// <summary>The panel the cards are in, once the layout has made it. Null in the other layouts.</summary>
    public OldSchoolPanel? Panel => Visuals.Child<OldSchoolPanel>(Items);
}
