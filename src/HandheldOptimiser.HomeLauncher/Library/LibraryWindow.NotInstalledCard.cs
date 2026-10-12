using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Old School layout's Not installed card, opened: the card grows to nearly the size of the screen,
/// and the games that are not installed are a grid that scrolls inside it, with their search in its
/// heading. B, or the arrow in the heading, shrinks it back to the card it was, so the cards are never
/// left for another screen. The other layouts have these games on a tab of their own.
/// </summary>
public partial class LibraryWindow
{
    private static readonly Duration CardGrow = TimeSpan.FromMilliseconds(240);
    private static readonly Duration CardShrink = TimeSpan.FromMilliseconds(200);
    private static readonly Duration CardFade = TimeSpan.FromMilliseconds(140);

    // The page's own edges, as LibraryWindow.xaml has them, and its place inside the open card: below
    // the card's heading and within its sides. Both leave the games the same width, so the grid has
    // as many columns in the card as it has on its tab in the other layouts.
    private static readonly Thickness PageEdges = new(38, 4, 38, 24);

    // Under the Old School cards there is only the button hints, so the page runs nearly down to them,
    // which is the room the list of categories needs above the cards on the handheld's own screen.
    private static readonly Thickness CardEdges = new(38, 4, 38, 8);
    private static readonly Thickness OpenCardPage = new(24, 60, 24, 10);
    private static readonly Thickness OpenCardEdges = new(14, 4, 14, 10);

    // Whether the page is laid out inside the open card, and where the small card was when it was
    // opened, which the big one grows from.
    private bool _cardOpen;
    private Rect? _cardFrom;

    // Bumped whenever the card starts to grow or shrink, so an older move does not finish over a newer.
    private int _cardMove;

    // Set while the card grows, before its games are there, when the controller and the keys are left alone.
    private bool _cardGrowing;

    /// <summary>Whether the Old School layout's Not installed card is open, with the grid inside it.</summary>
    private bool IsBrowsing => _layout == LibraryLayout.OldSchool && _filter.NotInstalled;

    /// <summary>
    /// Opens the Not installed card. It grows from its place among the cards at once, and the games are
    /// only made when it has: there may be hundreds, and making them holds everything up for a moment,
    /// which is better spent looking at the open card than at a press that seems to have done nothing.
    /// </summary>
    private void BrowseNotInstalled()
    {
        if (_cardGrowing)
        {
            return;
        }

        _cardFrom = FrontCardPlace();
        OpenCard.Visibility = Visibility.Visible;
        if (_cardFrom is not { } from || CardAt(from) is not { } at)
        {
            ShowNotInstalled();
            return;
        }

        // Nothing is pressed or moved to on the way, as the cards underneath are on their way out.
        _cardGrowing = true;
        Scroller.IsHitTestVisible = false;

        var (scale, move, small) = at;
        scale.ScaleX = small.ScaleX;
        scale.ScaleY = small.ScaleY;
        move.X = small.X;
        move.Y = small.Y;

        var turn = ++_cardMove;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(1, CardGrow) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (turn == _cardMove)
            {
                ShowNotInstalled();
            }
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, CardGrow) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, CardGrow) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, CardGrow) { EasingFunction = ease });
        Scroller.BeginAnimation(OpacityProperty, new DoubleAnimation(0, CardFade));
    }

    /// <summary>Fills the open card with the games that are not installed, which fade in once they have been drawn.</summary>
    private void ShowNotInstalled()
    {
        // Another window came in front while the card grew: it would only be put away again.
        if (!IsActive)
        {
            SettleCard();
            return;
        }

        _filterBeforeBrowsing = _filter;
        _filter = TileFilter.Uninstalled;
        ApplyFilter();
        if (!_cardOpen)
        {
            // The last of them was installed or hidden meanwhile, and there is nothing to open.
            SettleCard();
            return;
        }

        RestoreFocus();
        Scroller.Opacity = OpenCardHeading.Opacity = 0;

        var turn = ++_cardMove;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (turn != _cardMove)
            {
                return;
            }

            var fade = new DoubleAnimation(1, CardFade);
            fade.Completed += (_, _) =>
            {
                if (turn == _cardMove)
                {
                    SettleCard();
                }
            };

            OpenCardHeading.BeginAnimation(OpacityProperty, new DoubleAnimation(1, CardFade));
            Scroller.BeginAnimation(OpacityProperty, fade);
        });
    }

    /// <summary>Back from the open Not installed card to the cards, with the focus on the card it grew from.</summary>
    private bool LeaveNotInstalled()
    {
        if (!IsBrowsing)
        {
            return false;
        }

        _filter = _filterBeforeBrowsing;
        GameCards.Fresh = true;
        ApplyFilter();

        var at = _categories.FindIndex(c => c.Kind == CategoryKind.NotInstalled);
        if (at >= 0)
        {
            _category = at;
            MoveCategoryList(at, animate: false);
            ShowCategoryCards();
            UpdateDeals();
            QueueCardReset();
        }

        RestoreFocus(leavingTopBar: true);
        ShrinkCard();
        return true;
    }

    private void OnCloseOpenCard(object sender, RoutedEventArgs e) => LeaveNotInstalled();

    /// <summary>
    /// Lays the page out inside the open card, or as it is without one, when which of the two it should
    /// be has changed: the card was opened or closed, or the layout changed under it.
    /// </summary>
    private void ShowOpenCard()
    {
        var open = IsBrowsing;
        if (open == _cardOpen)
        {
            return;
        }

        _cardOpen = open;
        SettleCard();

        OpenCardHeading.Visibility = BackHint.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Margin = open ? OpenCardPage : default;
        MoveSearch(open ? OpenCardSearch : TopBarCentre);
    }

    /// <summary>How many games the open card holds, or how many of them a search has found.</summary>
    private void UpdateOpenCardCount()
    {
        if (!IsBrowsing)
        {
            return;
        }

        var owned = ShownTiles().Count(t => !t.IsInstalled);
        OpenCardCount.Text = _tiles.Count != owned ? $"{_tiles.Count} of {owned} games"
            : owned == 1 ? "1 game"
            : $"{owned} games";
    }

    /// <summary>
    /// Moves the search button, its bar and its store button between the top bar and the open card's
    /// heading. The Old School layout has no top bar, and these games are the only ones searched.
    /// </summary>
    private void MoveSearch(Panel to)
    {
        if (ReferenceEquals(SearchButton.Parent, to))
        {
            return;
        }

        FrameworkElement[] parts = [StoreButton, SearchPanel, SearchButton];
        foreach (var part in parts)
        {
            (part.Parent as Panel)?.Children.Remove(part);
        }

        // In the top bar they come straight after the tabs.
        var at = ReferenceEquals(to, TopBarCentre) ? TopBarCentre.Children.IndexOf(FilterStrip) + 1 : 0;
        for (var i = 0; i < parts.Length; i++)
        {
            to.Children.Insert(at + i, parts[i]);
        }
    }

    /// <summary>Where the card at the front of the green cards is drawn, which the open card grows from and shrinks to.</summary>
    private Rect? FrontCardPlace() =>
        ActionStack.Panel is { IsVisible: true } panel
            ? panel.TransformToVisual(ScaledContent).TransformBounds(panel.FrontCard)
            : null;

    /// <summary>
    /// Shrinks the card back to the small one it was, which is among the cards again by now and fades
    /// in over it with the rest.
    /// </summary>
    private void ShrinkCard()
    {
        // Put away behind another window, where there is no one to watch it shrink.
        if (!IsActive)
        {
            SettleCard();
            return;
        }

        // The cards are laid out first, to see where the small one is.
        ScaledContent.UpdateLayout();
        if ((FrontCardPlace() ?? _cardFrom) is not { } to)
        {
            return;
        }

        OpenCard.Visibility = Visibility.Visible;
        if (CardAt(to) is not { } at)
        {
            SettleCard();
            return;
        }

        var (scale, move, small) = at;
        Scroller.Opacity = 0;
        var turn = ++_cardMove;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(small.ScaleX, CardShrink) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(small.ScaleY, CardShrink) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(small.X, CardShrink) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(small.Y, CardShrink) { EasingFunction = ease });

        var fade = new DoubleAnimation(1, CardShrink) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (turn == _cardMove)
            {
                SettleCard();
            }
        };

        Scroller.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Gives the open card's back the transforms that move it, at rest to begin with, and works out the
    /// ones that would draw it over a small card: how much smaller, and how far from its own corner.
    /// </summary>
    private (ScaleTransform Scale, TranslateTransform Move, (double ScaleX, double ScaleY, double X, double Y) Small)? CardAt(Rect small)
    {
        // Measured at rest, as its place is worked out with its own transform in it.
        OpenCardBack.RenderTransform = Transform.Identity;
        ScaledContent.UpdateLayout();

        var full = new Rect(OpenCardBack.TranslatePoint(default, ScaledContent), OpenCardBack.RenderSize);
        if (full.Width <= 0 || full.Height <= 0 || small.Width <= 0 || small.Height <= 0)
        {
            return null;
        }

        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        OpenCardBack.RenderTransform = new TransformGroup { Children = { scale, move } };
        return (scale, move, (small.Width / full.Width, small.Height / full.Height, small.X - full.X, small.Y - full.Y));
    }

    /// <summary>Ends a move that is part way: the card is fully open or gone, and the page fully there.</summary>
    private void SettleCard()
    {
        _cardMove++;
        _cardGrowing = false;
        Scroller.IsHitTestVisible = true;
        OpenCardBack.RenderTransform = Transform.Identity;
        OpenCard.Visibility = _cardOpen ? Visibility.Visible : Visibility.Collapsed;

        foreach (var faded in new UIElement[] { Scroller, OpenCardHeading })
        {
            faded.BeginAnimation(OpacityProperty, null);
            faded.Opacity = 1;
        }
    }
}
