using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// How the menu comes on screen. In the grid and the row it is a panel over the dimmed library. In the
/// Old School layout the card that was pressed turns over and the menu is its other side: in the card's
/// own place, at the card's own size and in its colours, so the cards are never left for another screen.
/// </summary>
public partial class LibraryWindow
{
    // A card turns edge on, and its other side turns to face the player.
    private static readonly Duration TurnAway = TimeSpan.FromMilliseconds(130);
    private static readonly Duration TurnToFace = TimeSpan.FromMilliseconds(170);

    // The menu as LibraryWindow.xaml lays it out over the dimmed library.
    private const double MenuWidth = 560;
    private const double MenuTitleSize = 22;
    private static readonly Thickness MenuMargin = new(0, 24, 0, 24);
    private static readonly Thickness MenuPadding = new(28, 24, 28, 20);
    private static readonly Thickness MenuStoreMargin = new(0, 2, 0, 18);
    private static readonly Brush MenuDim = FrozenBrush("#C0000000");
    private static readonly Brush MenuBack = FrozenBrush("#181B22");
    private static readonly Brush MenuText = FrozenBrush("#F2F3F5");

    // The menu on the back of a card, which has a good deal less room: tighter edges and a smaller
    // heading, and rows the card's colours show through, with the one in focus picked out in white.
    private const double CardTitleSize = 19;
    private static readonly Thickness CardPadding = new(14, 12, 14, 6);
    private static readonly Thickness CardStoreMargin = new(0, 0, 0, 10);
    private static readonly Brush CardRow = FrozenBrush("#38000000");
    private static readonly Brush CardRowFocus = FrozenBrush("#40FFFFFF");
    private static readonly Brush CardBack = FrozenBrush("#262B36");

    // Boxed once: the rows look them up as the resources they are in LibraryWindow.xaml.
    private static readonly object CardRowPadding = new Thickness(14, 6, 14, 6);
    private static readonly object CardRowMargin = new Thickness(0, 0, 0, 6);
    private static readonly object CardRowHeight = 42d;

    // How much of a game's own colours the back of its card keeps: darkened, so white text reads on it.
    private const double CardBackShade = 0.62;

    // The card that has turned over to show the menu, the one on its way back, and whether the menu is
    // one of the settings' lists opened from its own card, which Back turns over again.
    private Button? _turned;
    private Button? _turningBack;
    private bool _menuFromCard;

    // Bumped whenever a turn begins, so the second half of an older one does not finish over a newer.
    private int _turn;

    /// <summary>Whether the menu is on the back of an Old School card, and so short of room.</summary>
    private bool MenuOnCard => _turned is not null;

    /// <summary>Brings the menu on screen: over the dimmed library, or as the other side of the front Old School card.</summary>
    private void ShowMenuOverlay()
    {
        // Already up: its lists change in place.
        if (MenuOverlay.Visibility == Visibility.Visible && MenuOverlay.IsHitTestVisible)
        {
            return;
        }

        StopTurn();
        MenuOverlay.Visibility = Visibility.Visible;
        MenuOverlay.IsHitTestVisible = true;

        if (IsOldSchool && OpenStack is { Panel: { } panel } stack && FrontCard(stack) is { } card)
        {
            TurnToMenu(panel, card);
            return;
        }

        MenuOverlay.Background = MenuDim;
        MenuBox.HorizontalAlignment = HorizontalAlignment.Center;
        MenuBox.VerticalAlignment = VerticalAlignment.Center;
        MenuBox.Margin = MenuMargin;
        MenuBox.Padding = MenuPadding;
        MenuBox.Width = MenuWidth;
        MenuBox.Height = double.NaN;
        MenuBox.Background = MenuBack;
        MenuBox.LayoutTransform = Transform.Identity;
        MenuBox.RenderTransform = Transform.Identity;
        foreach (var key in new[] { "MenuRowBrush", "MenuRowFocusBrush", "MenuRowPadding", "MenuRowMargin", "MenuRowHeight" })
        {
            MenuBox.Resources.Remove(key);
        }

        TextElement.SetForeground(MenuBox, MenuText);
        MenuTitle.FontSize = MenuTitleSize;
        MenuStore.Margin = MenuStoreMargin;

        // Over the open Not installed card the menu is the size it is in the other layouts, as there is
        // no small card to turn over, and takes the card's green and its see-through rows.
        if (IsBrowsing)
        {
            MenuBox.Background = (Brush)FindResource("OldSchoolGreenBack");
            MenuBox.Resources["MenuRowBrush"] = CardRow;
            MenuBox.Resources["MenuRowFocusBrush"] = CardRowFocus;
            TextElement.SetForeground(MenuBox, Brushes.White);
        }
    }

    /// <summary>Takes the menu off screen again. An Old School card that turned over to show it turns back.</summary>
    private void HideMenuOverlay()
    {
        _menuFromCard = false;
        if (_turned is not { } card || MenuBox.RenderTransform is not ScaleTransform { IsFrozen: false } menu)
        {
            StopTurn();
            MenuOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // The menu turns edge on and goes, and the card turns back to face the player. Nothing in it
        // can be pressed on the way.
        _turned = null;
        _turningBack = card;
        MenuOverlay.IsHitTestVisible = false;
        var turn = ++_turn;
        var away = new DoubleAnimation(0, TurnAway) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            if (turn != _turn)
            {
                return;
            }

            MenuOverlay.Visibility = Visibility.Collapsed;
            if (card.RenderTransform is ScaleTransform { IsFrozen: false } face)
            {
                face.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, TurnToFace) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        };

        menu.BeginAnimation(ScaleTransform.ScaleXProperty, away);
    }

    /// <summary>
    /// Turns a card over to show the menu. The menu is put exactly where the card is, at the card's own
    /// size, and scrolls within that when it has more rows than fit. It takes the card's colours: the
    /// green of a settings or power card, or a game's own colours for its quick actions.
    /// </summary>
    private void TurnToMenu(OldSchoolPanel panel, Button card)
    {
        var fit = panel.Fit;
        var front = panel.TransformToVisual(ScaledContent).TransformBounds(panel.FrontCard);

        // No dimming: the rest of the cards stay as they are around the one that turned. A press
        // outside the card still closes the menu.
        MenuOverlay.Background = Brushes.Transparent;
        MenuBox.HorizontalAlignment = HorizontalAlignment.Left;
        MenuBox.VerticalAlignment = VerticalAlignment.Top;
        MenuBox.Margin = new Thickness(Math.Max(0, front.Left), Math.Max(0, front.Top), 0, 0);

        // Laid out at the card's own size and drawn as much bigger as the card is, as the card's face is.
        MenuBox.Width = OldSchoolStack.CardWidth;
        MenuBox.Height = panel.CardHeight;
        MenuBox.Padding = CardPadding;
        MenuBox.LayoutTransform = new ScaleTransform(fit, fit);

        MenuBox.Background = card.DataContext switch
        {
            ActionCard => (Brush)FindResource("OldSchoolGreenBack"),
            GameTile tile => GameBack(tile),
            _ => CardBack
        };

        MenuBox.Resources["MenuRowBrush"] = CardRow;
        MenuBox.Resources["MenuRowFocusBrush"] = CardRowFocus;
        MenuBox.Resources["MenuRowPadding"] = CardRowPadding;
        MenuBox.Resources["MenuRowMargin"] = CardRowMargin;
        MenuBox.Resources["MenuRowHeight"] = CardRowHeight;
        TextElement.SetForeground(MenuBox, Brushes.White);
        MenuTitle.FontSize = CardTitleSize;
        MenuStore.Margin = CardStoreMargin;

        // Both turn about their middle, which is the same place, so the one is seen to become the other.
        var menu = new ScaleTransform(0, 1);
        MenuBox.RenderTransformOrigin = new Point(0.5, 0.5);
        MenuBox.RenderTransform = menu;

        var face = new ScaleTransform(1, 1);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = face;
        _turned = card;

        var turn = ++_turn;
        var away = new DoubleAnimation(0, TurnAway) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            if (turn == _turn)
            {
                menu.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, TurnToFace) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        };

        face.BeginAnimation(ScaleTransform.ScaleXProperty, away);
    }

    /// <summary>A game's own colours for the back of its card, darkened so white text reads on them.</summary>
    private static Brush GameBack(GameTile tile)
    {
        static Color Shade(Color colour) => Color.FromRgb(
            (byte)(colour.R * CardBackShade), (byte)(colour.G * CardBackShade), (byte)(colour.B * CardBackShade));

        var (from, to) = tile.BackdropColours;
        var brush = new LinearGradientBrush(Shade(from), Shade(to), 45);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The button hints at the foot of a list of the menu, which the back of a card has no room for: the
    /// same buttons are along the bottom of the screen.
    /// </summary>
    private void ShowMenuHints(StackPanel list)
    {
        foreach (var hints in list.Children.OfType<StackPanel>())
        {
            hints.Visibility = MenuOnCard ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Ends a turn that is part way, with the card facing the player again.</summary>
    private void StopTurn()
    {
        _turn++;
        foreach (var card in new[] { _turned, _turningBack })
        {
            if (card is not null)
            {
                card.RenderTransform = Transform.Identity;
            }
        }

        _turned = _turningBack = null;
        MenuBox.RenderTransform = Transform.Identity;
    }

    /// <summary>The card at the front of a stack.</summary>
    private static Button? FrontCard(CardStack stack) =>
        ItemContainers.At(stack.Items, stack.Front) is { } container
            ? VisualChild<Button>(container)
            : null;
}
