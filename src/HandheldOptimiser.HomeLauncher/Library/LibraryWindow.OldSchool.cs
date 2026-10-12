using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Old School layout: the games, and the deals below them, each as a staggered line of big cards
/// like the Xbox 360's, that move along as the focus does or as they are swiped. The games' wide artwork
/// is only held for the cards on screen, and the library only draws with the GPU while this layout is in
/// front, as the rest of it is drawn in software to stay small behind a game.
/// </summary>
public partial class LibraryWindow
{
    // Long enough to follow, short enough that holding a direction keeps up with the d-pad.
    private static readonly Duration CardMove = TimeSpan.FromMilliseconds(260);

    // How far back the cards start from when the layout or the tab is first shown, to slide in from.
    private const double CardSlideIn = 1.5;

    // Cards this far beyond the ones on screen keep their artwork, so a step back does not load it again.
    private const int CardArtKept = 3;

    // The most cards that hold artwork at once, and the ones that do, with the longest unseen first.
    // One more than are wanted at a time, which is the seven on screen or sliding on and off it and
    // the three kept either side: each picture is over a megabyte, and a category gone back to loads
    // its own again in a moment.
    private const int CardArtHeld = 14;
    private readonly List<GameTile> _cardArtHeld = [];

    // A click this soon after a swipe or a tap moved the cards is that being let go, not a press of the
    // card now under it.
    private static readonly TimeSpan SwipeClick = TimeSpan.FromMilliseconds(400);

    /// <summary>One stack of Old School cards: the games, or the deals.</summary>
    private sealed class CardStack(ItemsControl items)
    {
        public ItemsControl Items { get; } = items;

        /// <summary>The place of the card at the front, or on its way there.</summary>
        public int Front { get; set; } = -1;

        /// <summary>A card that a tap has just brought forward, whose click must not press it as well.</summary>
        public object? BroughtForward { get; set; }

        /// <summary>Set when the cards should slide in rather than move from where they were.</summary>
        public bool Fresh { get; set; }

        /// <summary>The panel the cards are in, once the layout has made it. Null in the other layouts.</summary>
        public OldSchoolPanel? Panel => VisualChild<OldSchoolPanel>(Items);
    }

    private CardStack? _gameCards;
    private CardStack? _dealCards;

    private CardStack GameCards => _gameCards ??= new CardStack(Tiles);

    private CardStack DealCards => _dealCards ??= new CardStack(DealTiles);

    // The games found to have no wide artwork since the library came to the front, so they are not asked about again.
    private readonly HashSet<string> _noCardArt = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cardArtLoad;

    // A press on a stack that may turn into a swipe: the stack, the finger (null for the mouse), the
    // card it landed on, and when a swipe or a tap last moved the cards.
    private readonly OldSchoolSwipe _swipe = new();
    private CardStack? _pressed;
    private int? _pressedTouch;
    private int _pressedCard = -1;
    private DateTime _cardsMovedAt = DateTime.MinValue;

    // Where a press in the Old School view began, in the window, and the finger it was: letting go far
    // enough up or down from there moves between the categories.
    private Point? _pressedAt;
    private int? _pressedFinger;

    // How far up or down a press has to move to change category.
    private const double CategorySwipe = 70;

    /// <summary>
    /// Whether the games are the Old School cards: that layout, but for the Not installed tab, whose
    /// hundreds of games are a grid in every layout.
    /// </summary>
    private bool IsOldSchool => _layout == LibraryLayout.OldSchool && !_filter.NotInstalled;

    /// <summary>Whether the games are in one line across, in the row or as the Old School cards.</summary>
    private bool IsOneLine => IsRow || IsOldSchool;

    /// <summary>Brings a card to the front of its stack, with the ones before it sliding away and the rest moving up.</summary>
    private void ShowCard(CardStack stack, int index)
    {
        var count = stack.Items.Items.Count;
        if (!IsOldSchool || stack.Panel is not { } panel || index < 0 || index >= count)
        {
            return;
        }

        stack.Front = index;

        if (stack.Fresh)
        {
            stack.Fresh = false;
            panel.BeginAnimation(OldSchoolPanel.PositionProperty, null);
            panel.Position = index - CardSlideIn;
        }

        // With no start given it sets off from wherever the cards are, part way through a move or not.
        var move = new DoubleAnimation(index, CardMove) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
        {
            // Software rendering redraws the cards for each step, so take fewer of them.
            Timeline.SetDesiredFrameRate(move, 30);
        }

        panel.BeginAnimation(OldSchoolPanel.PositionProperty, move);
    }

    /// <summary>
    /// A card has been given the focus, which brings it to the front. The mouse and touch give a card
    /// the focus as they press it, before the click arrives, so a card they bring forward is remembered:
    /// that click must not press it as well.
    /// </summary>
    private void CardFocused(CardStack stack, object card, int index)
    {
        stack.BroughtForward = Mouse.LeftButton == MouseButtonState.Pressed && index != stack.Front ? card : null;
        ShowCard(stack, index);
    }

    /// <summary>
    /// Whether a click on a card should do nothing: it brought the card forward, or it is a swipe being
    /// let go over the card.
    /// </summary>
    private bool CardClickSwallowed(CardStack stack, object card)
    {
        if (!IsOldSchool)
        {
            return false;
        }

        if (ReferenceEquals(stack.BroughtForward, card))
        {
            stack.BroughtForward = null;
            return true;
        }

        return DateTime.UtcNow - _cardsMovedAt < SwipeClick;
    }

    /// <summary>Puts each stack's card at the front once the cards exist, after the games, the deals or the layout changed.</summary>
    private void QueueCardReset()
    {
        if (!IsOldSchool)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Only the open category's cards are on screen.
            switch (OpenKind)
            {
                case CategoryKind.Games:
                    ShowCard(GameCards, Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey)));
                    LoadCardArt(atOnce: true);
                    break;

                // The deal the focus was last on, or where the stack was, as far as there are still deals.
                case CategoryKind.Deals when _deals is { Count: > 0 } deals:
                    var deal = deals.FindIndex(d => d.Deal.Id == _focusedDealId);
                    ShowCard(DealCards, Math.Clamp(deal >= 0 ? deal : DealCards.Front, 0, deals.Count - 1));
                    break;

                case { } kind when ActionCards.Items.Count > 0:
                    ShowCard(ActionStack, Math.Clamp(_actionAt.GetValueOrDefault(kind), 0, ActionCards.Items.Count - 1));
                    break;
            }
        });
    }

    // ----- Swiping along a stack -----

    /// <summary>
    /// Follows touch and the mouse across the window, for swipes along the stacks. Watched from the
    /// window, as the page takes hold of a finger for its own scrolling as soon as it lands.
    /// </summary>
    private void WatchCardSwipes()
    {
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is null && InCategoryView(e.OriginalSource))
            {
                _pressedAt = e.GetPosition(this);
            }

            if (StackUnder(e.OriginalSource) is not { Panel: { } panel } stack)
            {
                return;
            }

            // A press on a card behind the front one is kept from its button, which would take the
            // focus at once and bring the card forward before a swipe had the chance to begin. Letting
            // go without swiping brings it forward instead.
            var card = CardUnder(stack, e.OriginalSource);
            if (card != stack.Front)
            {
                e.Handled = true;
            }

            // Touch arrives as the mouse as well, and is followed as touch.
            if (e.StylusDevice is null)
            {
                BeginPress(stack, null, card, e.GetPosition(panel));
            }
        };

        PreviewMouseMove += (_, e) =>
        {
            if (_pressed is not { Panel: { } panel } stack || _pressedTouch is not null || e.StylusDevice is not null)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndPress();
            }
            else if (MovePress(e.GetPosition(panel), () => stack.Items.CaptureMouse()))
            {
                e.Handled = true;
            }
        };

        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.StylusDevice is not null)
            {
                return;
            }

            if (_pressed is { } stack && _pressedTouch is null && EndPress())
            {
                _pressedAt = null;
                stack.Items.ReleaseMouseCapture();
                e.Handled = true;
            }
            else if (EndCategorySwipe(e.GetPosition(this)))
            {
                // The card under the press is let go of, so it is not pressed as well.
                Mouse.Capture(null);
                e.Handled = true;
            }
        };

        PreviewTouchDown += (_, e) =>
        {
            if (_pressedFinger is null && InCategoryView(e.OriginalSource))
            {
                _pressedFinger = e.TouchDevice.Id;
                _pressedAt = e.GetTouchPoint(this).Position;
            }

            if (_pressed is null && StackUnder(e.OriginalSource) is { Panel: { } panel } stack)
            {
                BeginPress(stack, e.TouchDevice.Id, CardUnder(stack, e.OriginalSource), e.GetTouchPoint(panel).Position);
            }
        };

        PreviewTouchMove += (_, e) =>
        {
            // Holding the finger for the cards takes it from the page, which would scroll with it otherwise.
            if (_pressed is { Panel: { } panel } stack && _pressedTouch == e.TouchDevice.Id
                && MovePress(e.GetTouchPoint(panel).Position, () => e.TouchDevice.Capture(stack.Items)))
            {
                e.Handled = true;
            }
        };

        PreviewTouchUp += (_, e) =>
        {
            var finger = _pressedFinger == e.TouchDevice.Id;
            if (finger)
            {
                _pressedFinger = null;
            }

            if (_pressed is { } stack && _pressedTouch == e.TouchDevice.Id && EndPress())
            {
                _pressedAt = null;
                stack.Items.ReleaseTouchCapture(e.TouchDevice);
                e.Handled = true;
            }
            else if (finger && EndCategorySwipe(e.GetTouchPoint(this).Position))
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>Whether a press landed in the Old School view: on the names, the cards or the space around them.</summary>
    private bool InCategoryView(object source) =>
        IsOldSchool && _menu is null && source is Visual pressed && Scroller.IsAncestorOf(pressed);

    /// <summary>
    /// A press has been let go. When it moved up or down the screen it was a swipe between the
    /// categories: the names follow the finger, so one moving down brings the name above into place.
    /// </summary>
    private bool EndCategorySwipe(Point at)
    {
        if (_pressedAt is not { } from)
        {
            return false;
        }

        _pressedAt = null;
        var across = Math.Abs(at.X - from.X);
        var down = at.Y - from.Y;
        if (!IsOldSchool || Math.Abs(down) < CategorySwipe || Math.Abs(down) < across * 1.5)
        {
            return false;
        }

        _cardsMovedAt = DateTime.UtcNow;
        StepCategory(down > 0 ? 1 : -1);
        return true;
    }

    /// <summary>The stack a press landed on, if it landed on one.</summary>
    private CardStack? StackUnder(object source) =>
        !IsOldSchool || _menu is not null || source is not Visual pressed ? null
        : Tiles.IsAncestorOf(pressed) ? GameCards
        : DealTiles.IsAncestorOf(pressed) ? DealCards
        : ActionCards.IsAncestorOf(pressed) ? ActionStack
        : null;

    /// <summary>Which of a stack's cards a press landed on, or -1 for none.</summary>
    private static int CardUnder(CardStack stack, object source)
    {
        for (var at = source as DependencyObject; at is not null && !ReferenceEquals(at, stack.Items); at = VisualTreeHelper.GetParent(at))
        {
            if (at is ContentPresenter container && stack.Items.ItemContainerGenerator.IndexFromContainer(container) is >= 0 and var card)
            {
                return card;
            }
        }

        return -1;
    }

    private void BeginPress(CardStack stack, int? touch, int card, Point at)
    {
        _pressed = stack;
        _pressedTouch = touch;
        _pressedCard = card;
        _swipe.Begin(at.X, at.Y);
    }

    /// <summary>Moves the cards with a press that has become a swipe. True while it is one.</summary>
    /// <param name="hold">Takes hold of the mouse or the finger for the stack, as the swipe begins.</param>
    private bool MovePress(Point at, Action hold)
    {
        if (_pressed is not { Panel: { } panel } stack)
        {
            _pressed = null;
            return false;
        }

        var swiping = _swipe.Swiping;
        switch (_swipe.Move(panel, stack.Items.Items.Count, at.X, at.Y, Environment.TickCount64))
        {
            case null:
                return false;

            // Up or down: the page's to scroll.
            case false:
                _pressed = null;
                return false;
        }

        if (!swiping)
        {
            // The card under the press is let go of, so it is not pressed when the swipe ends.
            hold();
            stack.BroughtForward = null;
        }

        return true;
    }

    /// <summary>
    /// A press has been let go. A swipe settles the cards on the nearest one, and a tap on a card behind
    /// the front one brings it forward. True if it was either, and so not a press of the front card.
    /// </summary>
    private bool EndPress()
    {
        var stack = _pressed;
        _pressed = null;
        _pressedTouch = null;
        if (stack?.Panel is not { } panel)
        {
            return false;
        }

        int card;
        if (_swipe.Swiping)
        {
            card = _swipe.End(panel, stack.Items.Items.Count, Environment.TickCount64);
        }
        else if (_pressedCard >= 0 && _pressedCard != stack.Front)
        {
            card = _pressedCard;
        }
        else
        {
            return false;
        }

        // Touch presses the card under it once the finger has lifted, which must not press it as well.
        _cardsMovedAt = DateTime.UtcNow;

        // Giving it the focus brings it to the front. The card that already has the focus raises no
        // focus event, so the cards are sent to it as well.
        if (stack.Items.ItemContainerGenerator.ContainerFromIndex(card) is ContentPresenter container)
        {
            VisualChild<Button>(container)?.Focus();
        }

        stack.BroughtForward = null;
        ShowCard(stack, card);
        return true;
    }

    // ----- The games' wide artwork -----

    /// <summary>
    /// Gives the cards on screen their wide artwork, once the focus has settled, and lets go of the
    /// artwork of the cards far from it. Each card changes as its own arrives.
    /// </summary>
    /// <param name="atOnce">Without waiting for the focus to settle, for cards that have just come on screen.</param>
    private async void LoadCardArt(bool atOnce = false)
    {
        if (!IsOldSchool || !IsActive || _tiles.Count == 0)
        {
            return;
        }

        _cardArtLoad?.Cancel();
        var load = _cardArtLoad = new CancellationTokenSource();
        if (!atOnce)
        {
            try
            {
                await Task.Delay(BackdropDelay, load.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var tiles = _tiles;
        var front = Math.Max(0, tiles.FindIndex(t => t.Game.Key == _focusedKey));
        var (first, last) = OldSchoolStack.Window(front, tiles.Count);

        // The cards on screen and the few either side are the ones wanted now. The rest keep their
        // artwork until there is more of it held than a couple of screens of cards, the longest unseen
        // going first, so going back to a category just left does not load it all again.
        var wanted = tiles.Skip(first - CardArtKept).Take(last - first + 1 + (2 * CardArtKept)).ToList();
        _cardArtHeld.RemoveAll(wanted.Contains);
        _cardArtHeld.RemoveAll(t => t.Wide is null);
        _cardArtHeld.AddRange(wanted);
        while (_cardArtHeld.Count > CardArtHeld)
        {
            _cardArtHeld[0].ShowWide(null);
            _cardArtHeld.RemoveAt(0);
        }

        var (width, height) = CardPixels();
        var decoded = false;

        // The front card first, then outwards from it.
        foreach (var tile in tiles.Skip(first).Take(last - first + 1).OrderBy(t => Math.Abs(tiles.IndexOf(t) - front)))
        {
            if (tile.Wide is not null || !tile.IsInstalled || _noCardArt.Contains(tile.Game.Key))
            {
                continue;
            }

            // An app has a landscape picture with its logo on it, shown whole. One with none keeps its
            // cover on its own colours: the banner artwork kept for programs is a backdrop with no logo.
            var art = await Task.Run(async () => tile.IsApp
                ? await Artwork.FindCapsuleAsync(tile.Game) is { } capsule ? Backdrops.AppCard(capsule, width, height) : null
                : await Artwork.FindAsync(tile.Game) is { } path ? Backdrops.Card(path, width, height) : null);

            // The layout may have changed or a game come to the front meanwhile.
            if (!IsOldSchool || !IsActive)
            {
                break;
            }

            if (art is null)
            {
                _noCardArt.Add(tile.Game.Key);
            }
            else
            {
                tile.ShowWide(art);
                decoded = true;
            }

            // The focus has moved on, and the load that followed it takes over from here.
            if (load.IsCancellationRequested)
            {
                break;
            }
        }

        if (decoded)
        {
            // As for the background: decoding artwork leaves buffers behind that only a collection frees.
            GC.Collect();
        }
    }

    /// <summary>Lets go of the cards' artwork, while a game is in front or another layout is shown.</summary>
    private void UnloadCardArt()
    {
        _cardArtLoad?.Cancel();
        _noCardArt.Clear();
        _cardArtHeld.Clear();
        foreach (var tile in _allTiles)
        {
            tile.ShowWide(null);
        }
    }

    /// <summary>A game's card's size in real pixels on this screen, which is what its artwork is made at.</summary>
    private (int Width, int Height) CardPixels()
    {
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var drawn = _scale * (GameCards.Panel?.Fit ?? 1);
        return ((int)Math.Round(OldSchoolStack.CardWidth * drawn * toDevice.M11),
            (int)Math.Round(OldSchoolStack.CardHeight * drawn * toDevice.M22));
    }

    /// <summary>
    /// Draws with the GPU while the Old School layout is in front, where moving the cards every frame is
    /// more than software rendering keeps up with, and in software the rest of the time, which keeps the
    /// library small and the graphics driver out of it behind a game. The open Not installed card is
    /// part of that layout: changing over as it opens and closes would stall it as it grows.
    /// </summary>
    private void UpdateRenderMode()
    {
        var mode = _layout == LibraryLayout.OldSchool && IsActive ? RenderMode.Default : RenderMode.SoftwareOnly;
        if (RenderOptions.ProcessRenderMode == mode)
        {
            return;
        }

        RenderOptions.ProcessRenderMode = mode;

        using var process = Process.GetCurrentProcess();
        Program.Log($"Drawing {(mode == RenderMode.Default ? "with the GPU" : "in software")}, at {process.PrivateMemorySize64 / (1024 * 1024)} MB");
    }
}
