using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The kinds of category in the Old School layout's list.</summary>
internal enum CategoryKind
{
    Games,
    NotInstalled,
    Deals,
    Settings,
    Power
}

/// <summary>The parts of the window the Old School layout draws in.</summary>
internal sealed record OldSchoolParts(
    ItemsControl Categories,
    ItemsControl Tiles,
    ItemsControl DealTiles,
    ItemsControl ActionCards,
    ScrollViewer Scroller,
    ScrollViewer TilesScroller,
    StackPanel Page,
    Grid OpenCard,
    Border OpenCardBack,
    FrameworkElement OpenCardHeading,
    TextBlock OpenCardCount,
    Panel OpenCardSearch,
    Button CloseOpenCardButton,
    FrameworkElement BackHint,
    FrameworkElement ScaledContent,
    TextBlock AcceptHint,
    FrameworkElement OptionsHint,
    TopBarView TopBar,
    MenuView Menu);

/// <summary>
/// The Old School layout, the Xbox 360's look: a list of categories above one stack of staggered cards. Up
/// and down move between the categories, in place of the tabs and buttons along the top, and the cards below
/// are the open one's: the games of a tab, the deals, or green cards for the settings and for power. The cards
/// move along as the focus does or as they are swiped. The games' wide artwork is only held for the cards on
/// screen, and the library only draws with the GPU while this layout is in front, as the rest of it is drawn
/// in software to stay small behind a game.
///
/// The Not installed card, opened, grows to nearly the size of the screen, and the games that are not
/// installed are a grid that scrolls inside it, with their search in its heading. B, or the arrow in the
/// heading, shrinks it back to the card it was, so the cards are never left for another screen.
/// </summary>
internal sealed class OldSchoolPresenter
{
    private readonly Window _window;
    private readonly OldSchoolParts _parts;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly LibraryNavigation _navigation;
    private readonly LibraryLayoutManager _layouts;
    private readonly NoticePresenter _notices;
    private readonly BackdropPresenter _backdrop;
    private readonly TileArtwork _artwork;
    private readonly TopBarPresenter _topBar;
    private readonly SettingsMenu _settings;
    private readonly PowerMenu _power;
    private readonly ILibraryHost _host;
    private readonly Func<List<DealTile>?> _deals;
    private readonly Func<string?> _focusedDealId;
    private readonly Action _updateDeals;
    private readonly Action _updateEmptyText;

    /// <param name="deals">The deals on show, or null when there are none or the row is off.</param>
    /// <param name="focusedDealId">The deal the focus was last on.</param>
    /// <param name="updateDeals">Shows the deals for the category or tab that is open.</param>
    /// <param name="updateEmptyText">Says what is missing when there is nothing to show.</param>
    public OldSchoolPresenter(Window window, OldSchoolParts parts, LibraryState state, LibraryOptions options,
        LibraryNavigation navigation, LibraryLayoutManager layouts, NoticePresenter notices, BackdropPresenter backdrop,
        TileArtwork artwork, TopBarPresenter topBar, SettingsMenu settings, PowerMenu power, ILibraryHost host,
        Func<List<DealTile>?> deals, Func<string?> focusedDealId, Action updateDeals, Action updateEmptyText)
    {
        _window = window;
        _parts = parts;
        _state = state;
        _options = options;
        _navigation = navigation;
        _layouts = layouts;
        _notices = notices;
        _backdrop = backdrop;
        _artwork = artwork;
        _topBar = topBar;
        _settings = settings;
        _power = power;
        _host = host;
        _deals = deals;
        _focusedDealId = focusedDealId;
        _updateDeals = updateDeals;
        _updateEmptyText = updateEmptyText;

        WatchCardSwipes();
        Visuals.OnItemButtons(parts.ActionCards, OnActionClick, OnActionFocused);
        parts.Categories.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCategoryPressed));
        parts.CloseOpenCardButton.Click += (_, _) => LeaveNotInstalled();
    }

    // ===== OldSchool =====

    // Long enough to follow, short enough that holding a direction keeps up with the d-pad.
    private static readonly Duration CardMove = TimeSpan.FromMilliseconds(260);

    // How far back the cards start from when the layout or the tab is first shown, to slide in from.
    private const double CardSlideIn = 1.5;

    // A click this soon after a swipe or a tap moved the cards is that being let go, not a press of the
    // card now under it.
    private static readonly TimeSpan SwipeClick = TimeSpan.FromMilliseconds(400);

    private CardStack? _gameCards;
    private CardStack? _dealCards;

    public CardStack GameCards => _gameCards ??= new CardStack(_parts.Tiles);

    public CardStack DealCards => _dealCards ??= new CardStack(_parts.DealTiles);

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

    /// <summary>Brings a card to the front of its stack, with the ones before it sliding away and the rest moving up.</summary>
    public void ShowCard(CardStack stack, int index)
    {
        var count = stack.Items.Items.Count;
        if (!_layouts.IsOldSchool || stack.Panel is not { } panel || index < 0 || index >= count)
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
    public void CardFocused(CardStack stack, object card, int index)
    {
        stack.BroughtForward = Mouse.LeftButton == MouseButtonState.Pressed && index != stack.Front ? card : null;
        ShowCard(stack, index);
    }

    /// <summary>
    /// Whether a click on a card should do nothing: it brought the card forward, or it is a swipe being
    /// let go over the card.
    /// </summary>
    public bool CardClickSwallowed(CardStack stack, object card)
    {
        if (!_layouts.IsOldSchool)
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
    public void QueueCardReset()
    {
        if (!_layouts.IsOldSchool)
        {
            return;
        }

        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Only the open category's cards are on screen.
            switch (OpenKind)
            {
                case CategoryKind.Games:
                    ShowCard(GameCards, Math.Max(0, _state.FocusedIndex));
                    _artwork.LoadCardArt(atOnce: true);
                    break;

                // The deal the focus was last on, or where the stack was, as far as there are still deals.
                case CategoryKind.Deals when _deals() is { Count: > 0 } deals:
                    var deal = deals.FindIndex(d => d.Deal.Id == _focusedDealId());
                    ShowCard(DealCards, Math.Clamp(deal >= 0 ? deal : DealCards.Front, 0, deals.Count - 1));
                    break;

                case { } kind when _parts.ActionCards.Items.Count > 0:
                    ShowCard(ActionStack, Math.Clamp(_actionAt.GetValueOrDefault(kind), 0, _parts.ActionCards.Items.Count - 1));
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
        _window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is null && InCategoryView(e.OriginalSource))
            {
                _pressedAt = e.GetPosition(_window);
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

        _window.PreviewMouseMove += (_, e) =>
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

        _window.PreviewMouseLeftButtonUp += (_, e) =>
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
            else if (EndCategorySwipe(e.GetPosition(_window)))
            {
                // The card under the press is let go of, so it is not pressed as well.
                Mouse.Capture(null);
                e.Handled = true;
            }
        };

        _window.PreviewTouchDown += (_, e) =>
        {
            if (_pressedFinger is null && InCategoryView(e.OriginalSource))
            {
                _pressedFinger = e.TouchDevice.Id;
                _pressedAt = e.GetTouchPoint(_window).Position;
            }

            if (_pressed is null && StackUnder(e.OriginalSource) is { Panel: { } panel } stack)
            {
                BeginPress(stack, e.TouchDevice.Id, CardUnder(stack, e.OriginalSource), e.GetTouchPoint(panel).Position);
            }
        };

        _window.PreviewTouchMove += (_, e) =>
        {
            // Holding the finger for the cards takes it from the page, which would scroll with it otherwise.
            if (_pressed is { Panel: { } panel } stack && _pressedTouch == e.TouchDevice.Id
                && MovePress(e.GetTouchPoint(panel).Position, () => e.TouchDevice.Capture(stack.Items)))
            {
                e.Handled = true;
            }
        };

        _window.PreviewTouchUp += (_, e) =>
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
            else if (finger && EndCategorySwipe(e.GetTouchPoint(_window).Position))
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>Whether a press landed in the Old School view: on the names, the cards or the space around them.</summary>
    private bool InCategoryView(object source) =>
        _layouts.IsOldSchool && !_navigation.MenuOpen && source is Visual pressed && _parts.Scroller.IsAncestorOf(pressed);

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
        if (!_layouts.IsOldSchool || Math.Abs(down) < CategorySwipe || Math.Abs(down) < across * 1.5)
        {
            return false;
        }

        _cardsMovedAt = DateTime.UtcNow;
        StepCategory(down > 0 ? 1 : -1);
        return true;
    }

    /// <summary>The stack a press landed on, if it landed on one.</summary>
    private CardStack? StackUnder(object source) =>
        !_layouts.IsOldSchool || _navigation.MenuOpen || source is not Visual pressed ? null
        : _parts.Tiles.IsAncestorOf(pressed) ? GameCards
        : _parts.DealTiles.IsAncestorOf(pressed) ? DealCards
        : _parts.ActionCards.IsAncestorOf(pressed) ? ActionStack
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
        if (ItemContainers.At(stack.Items, card) is { } container)
        {
            Visuals.Child<Button>(container)?.Focus();
        }

        stack.BroughtForward = null;
        ShowCard(stack, card);
        return true;
    }

    /// <summary>A game's card's size in real pixels on this screen, which is what its artwork is made at.</summary>
    public (int Width, int Height) CardPixels()
    {
        var toDevice = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var drawn = _layouts.Scale * (GameCards.Panel?.Fit ?? 1);
        return ((int)Math.Round(OldSchoolStack.CardWidth * drawn * toDevice.M11),
            (int)Math.Round(OldSchoolStack.CardHeight * drawn * toDevice.M22));
    }

    /// <summary>
    /// Draws with the GPU while the Old School layout is in front, where moving the cards every frame is
    /// more than software rendering keeps up with, and in software the rest of the time, which keeps the
    /// library small and the graphics driver out of it behind a game. The open Not installed card is
    /// part of that layout: changing over as it opens and closes would stall it as it grows.
    /// </summary>
    public void UpdateRenderMode()
    {
        var mode = _layouts.Layout == LibraryLayout.OldSchool && _window.IsActive ? RenderMode.Default : RenderMode.SoftwareOnly;
        if (RenderOptions.ProcessRenderMode == mode)
        {
            return;
        }

        RenderOptions.ProcessRenderMode = mode;

        using var process = Process.GetCurrentProcess();
        Program.Log($"Drawing {(mode == RenderMode.Default ? "with the GPU" : "in software")}, at {process.PrivateMemorySize64 / (1024 * 1024)} MB");
    }

    // ===== Categories =====

    /// <summary>One name in the list. A tab of games carries the filter that shows them.</summary>
    private sealed record Category(string Name, CategoryKind Kind, TileFilter Filter = default);

    private static readonly Duration CategoryMove = TimeSpan.FromMilliseconds(240);

    private List<Category> _categories = [];
    private int _category;

    // Where the list is, or is on its way to. Not kept within the count when the list goes round.
    private double _categoryPosition;

    // The tab the games were on before the Not installed card was opened, to go back to.
    private TileFilter _filterBeforeBrowsing = TileFilter.All;

    // The card of each category that has cards of its own, where the focus was last.
    private readonly Dictionary<CategoryKind, int> _actionAt = [];
    private CardStack? _actionCards;

    // The Restart or Shut down card that has been pressed once and acts on the next press.
    private ActionCard? _armedCard;

    public CardStack ActionStack => _actionCards ??= new CardStack(_parts.ActionCards);

    /// <summary>The kind of category that is open, while the layout is Old School.</summary>
    public CategoryKind? OpenKind => _layouts.IsOldSchool && _category < _categories.Count ? _categories[_category].Kind : null;

    /// <summary>The stack of cards the open category shows.</summary>
    public CardStack? OpenStack => OpenKind switch
    {
        null => null,
        CategoryKind.Games => GameCards,
        CategoryKind.Deals => DealCards,
        _ => ActionStack
    };

    /// <summary>
    /// The categories, in order up the list: the deals when there are some, which puts them one step
    /// down from Games, where the library opens; then a tab for each kind of game, as the top bar has,
    /// the settings and power.
    /// </summary>
    private List<Category> BuildCategories()
    {
        var categories = new List<Category>();
        if (_deals() is { Count: > 0 })
        {
            categories.Add(new Category("Deals", CategoryKind.Deals));
        }

        categories.AddRange(TileFilter.Options(_state.Shown(_options.ShowHidden), _options.ShowFilter)
            .Select(f => f.NotInstalled ? new Category(f.Name, CategoryKind.NotInstalled, f) : new Category(f.Name, CategoryKind.Games, f)));

        categories.Add(new Category("Settings", CategoryKind.Settings));
        categories.Add(new Category("Power", CategoryKind.Power));
        return categories;
    }

    /// <summary>
    /// Makes the list of categories match the library, after the games or the deals changed, keeping the
    /// open one open, and shows the Old School view or puts it away for the layout in use.
    /// </summary>
    public void RefreshCategories()
    {
        if (_layouts.Layout != LibraryLayout.OldSchool)
        {
            ShowCategoryCards();
            return;
        }

        var categories = BuildCategories();
        if (!categories.SequenceEqual(_categories))
        {
            var open = _category < _categories.Count ? _categories[_category] : null;

            // The same one as before; or the tab the games are on.
            var at = open is null ? -1 : categories.IndexOf(open);
            if (at < 0)
            {
                at = Math.Max(0, categories.FindIndex(c => c.Kind == CategoryKind.Games && c.Filter == _state.Filter));
            }

            _categories = categories;
            _category = at;
            _parts.Categories.ItemsSource = categories;
            MoveCategoryList(at, animate: false);
        }

        ShowCategoryCards();
    }

    /// <summary>Opens the category for the tab the games are on, after the tab was changed some other way.</summary>
    public void OpenCategoryOfFilter()
    {
        var at = _categories.FindIndex(c => c.Kind is CategoryKind.Games or CategoryKind.NotInstalled && c.Filter == _state.Filter);
        if (_layouts.Layout == LibraryLayout.OldSchool && at >= 0 && at != _category)
        {
            _category = at;
            MoveCategoryList(at, animate: false);
        }
    }

    /// <summary>Moves the list so a category is the open one, by the shorter way round.</summary>
    private void MoveCategoryList(int to, bool animate)
    {
        if (Visuals.Child<OldSchoolListPanel>(_parts.Categories) is not { } panel)
        {
            // Not laid out yet: the list is put in place once it is.
            _categoryPosition = to;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (Visuals.Child<OldSchoolListPanel>(_parts.Categories) is { } made)
                {
                    made.BeginAnimation(OldSchoolListPanel.PositionProperty, null);
                    made.Position = _categoryPosition;
                }
            });
            return;
        }

        var count = _categories.Count;
        var from = OldSchoolList.Step((int)Math.Round(_categoryPosition), 0, count);
        _categoryPosition = animate ? Math.Round(_categoryPosition) + OldSchoolList.Steps(from, to, count) : to;

        if (!animate)
        {
            panel.BeginAnimation(OldSchoolListPanel.PositionProperty, null);
            panel.Position = _categoryPosition;
            return;
        }

        var move = new DoubleAnimation(_categoryPosition, CategoryMove) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        panel.BeginAnimation(OldSchoolListPanel.PositionProperty, move);
    }

    /// <summary>Up or down the list by one: the next category's cards take the place of this one's.</summary>
    public void StepCategory(int step)
    {
        var to = OldSchoolList.Step(_category, step, _categories.Count);
        if (to != _category)
        {
            OpenCategory(to);
        }
    }

    /// <summary>Opens a category: its name comes to the bottom of the list, and its cards slide in.</summary>
    private void OpenCategory(int index)
    {
        if (index < 0 || index >= _categories.Count || _navigation.MenuOpen)
        {
            return;
        }

        DisarmCard();
        _category = index;
        MoveCategoryList(index, animate: true);

        var category = _categories[index];
        _notices.Show(string.Empty);
        if (category.Kind == CategoryKind.Games && category.Filter != _state.Filter)
        {
            _state.Filter = category.Filter;
            GameCards.Fresh = true;
            _host.ShowTiles();
        }
        else
        {
            // Its cards slide in, as another tab's games do.
            if (OpenStack is { } stack)
            {
                stack.Fresh = true;
            }

            ShowCategoryCards();
            _updateDeals();
            _updateEmptyText();
            QueueCardReset();
        }

        _host.RestoreFocus(leavingTopBar: true);
    }

    /// <summary>Shows the open category's stack of cards and puts the others away, or the whole view when the layout is another.</summary>
    public void ShowCategoryCards()
    {
        var oldSchool = _layouts.IsOldSchool;
        var kind = OpenKind;

        // The names take the place of the tabs and buttons along the top, which keep their room so the clock stays put.
        // That goes for the open Not installed card as well, which has the search in its own heading.
        _parts.TopBar.TopBarCentre.Visibility = _layouts.Layout == LibraryLayout.OldSchool ? Visibility.Hidden : Visibility.Visible;
        _parts.Categories.Visibility = oldSchool ? Visibility.Visible : Visibility.Collapsed;
        ShowOpenCard();
        FitCategories();

        // The cards sit at the bottom of the screen, with the names above them. Nothing is left to scroll,
        // so a finger moving up or down is free to move between the categories.
        _parts.Page.VerticalAlignment = oldSchool ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        _parts.Scroller.PanningMode = oldSchool ? PanningMode.None : PanningMode.VerticalOnly;
        _parts.Scroller.Padding = _layouts.IsBrowsing ? OpenCardEdges : oldSchool ? CardEdges : PageEdges;

        var games = !oldSchool || kind == CategoryKind.Games;
        _parts.TilesScroller.Visibility = games ? Visibility.Visible : Visibility.Collapsed;

        // The quick actions are a game's, and there is nothing for X to open on the other cards.
        _parts.OptionsHint.Visibility = _options.QuickActions && games ? Visibility.Visible : Visibility.Collapsed;

        var actions = kind is CategoryKind.NotInstalled or CategoryKind.Settings or CategoryKind.Power;
        _parts.ActionCards.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        if (actions)
        {
            var cards = ActionsOf(kind!.Value);
            if (_parts.ActionCards.ItemsSource is not List<ActionCard> shown || !shown.Select(c => c.Name).SequenceEqual(cards.Select(c => c.Name)))
            {
                _parts.ActionCards.ItemsSource = cards;
            }
        }
        else
        {
            _parts.ActionCards.ItemsSource = null;
        }
    }

    /// <summary>Draws the names as much bigger as the cards are drawn to fill a wide screen, so the two keep their proportions.</summary>
    public void FitCategories()
    {
        if (!_layouts.IsOldSchool || _parts.Scroller.ViewportWidth <= 0)
        {
            return;
        }

        var fit = OldSchoolStack.Fit(_parts.Scroller.ViewportWidth - _parts.Tiles.Margin.Left - _parts.Tiles.Margin.Right);
        if (_parts.Categories.LayoutTransform is not ScaleTransform scale || Math.Abs(scale.ScaleX - fit) > 0.005)
        {
            _parts.Categories.LayoutTransform = new ScaleTransform(fit, fit);
        }
    }

    // ----- The cards of the categories that are not games or deals -----

    private List<ActionCard> ActionsOf(CategoryKind kind)
    {
        var cards = new List<ActionCard>();
        switch (kind)
        {
            case CategoryKind.NotInstalled:
                var owned = _state.Shown(_options.ShowHidden).Count(t => !t.IsInstalled);
                cards.Add(new ActionCard("", "Ready to Install",
                    owned == 1 ? "1 game you own, ready to install" : $"{owned} games you own, ready to install", BrowseNotInstalled));
                break;

            case CategoryKind.Settings:
                if (_topBar.Update is { } update)
                {
                    cards.Add(new ActionCard("", "Update now", $"Handheld Optimiser {Updates.Display(update)} is available",
                        () => _topBar.UpdateNow()));
                }

                cards.Add(new ActionCard("", SettingsMenu.LibraryDisplay, null,
                    () => _settings.OpenCategoryFromCard(_parts.Menu.LibraryDisplayItems)));
                cards.Add(new ActionCard("", SettingsMenu.GamesApps, null,
                    () => _settings.OpenCategoryFromCard(_parts.Menu.GamesAppsItems)));
                cards.Add(new ActionCard("", SettingsMenu.SleepTweaks, null,
                    () => _settings.OpenCategoryFromCard(_parts.Menu.SleepTweaksItems)));
                cards.Add(new ActionCard("", "Refresh library", null,
                    _settings.Rescan));
                cards.Add(new ActionCard("", "Close library", null, _settings.Quit));
                break;

            case CategoryKind.Power:
                cards.Add(new ActionCard("", "Sleep", null, _power.Sleep));
                if (Power.CanHibernate)
                {
                    cards.Add(new ActionCard("", "Hibernate", null, _power.Hibernate));
                }

                // These two take a second press, so a stray A cannot end a session.
                ActionCard? restart = null;
                ActionCard? shutDown = null;
                cards.Add(restart = new ActionCard("", "Restart", null, () => ConfirmCard(restart!, Power.Restart)));
                cards.Add(shutDown = new ActionCard("", "Shut down", null, () => ConfirmCard(shutDown!, Power.ShutDown)));
                break;
        }

        return cards;
    }

    /// <summary>
    /// Opens the settings or power category, for the Menu and View buttons: in the Old School layout
    /// those are cards like any others, in place of a menu over the library.
    /// </summary>
    public void OpenCategoryOf(CategoryKind kind)
    {
        var at = _categories.FindIndex(c => c.Kind == kind);
        if (at >= 0 && at != _category)
        {
            OpenCategory(at);
        }
    }

    /// <summary>Arms Restart or Shut down on its first press and acts on its second.</summary>
    private void ConfirmCard(ActionCard card, Func<string?> action)
    {
        if (!ReferenceEquals(_armedCard, card))
        {
            DisarmCard();
            _armedCard = card;
            card.Title = "Press again to confirm";
            return;
        }

        DisarmCard();
        Program.Log($"{card.Name} chosen from its card");

        _power.Act(action);
    }

    /// <summary>Moving off an armed card disarms it, so the second press is always a deliberate one.</summary>
    private void DisarmCard()
    {
        if (_armedCard is { } card)
        {
            card.Title = card.Name;
            _armedCard = null;
        }
    }

    private void OnActionFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button { DataContext: ActionCard card } || OpenKind is not { } kind)
        {
            return;
        }

        if (!ReferenceEquals(card, _armedCard))
        {
            DisarmCard();
        }

        var index = (_parts.ActionCards.ItemsSource as List<ActionCard>)?.IndexOf(card) ?? -1;
        _actionAt[kind] = index;
        _parts.AcceptHint.Text = "Select";
        CardFocused(ActionStack, card, index);
        _backdrop.ShowGreen();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        // A tap on a card behind the front one only brings it forward.
        if (sender is Button { DataContext: ActionCard card } && !CardClickSwallowed(ActionStack, card))
        {
            card.Act();
        }
    }

    /// <summary>A press on a category's name opens it. Everything drawn for the name has it as its data.</summary>
    private void OnCategoryPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Category category })
        {
            OpenCategory(_categories.IndexOf(category));
            e.Handled = true;
        }
    }

    /// <summary>Left and right along a stack of cards that has the focus.</summary>
    public void NavigateCards(CardStack stack, FocusNavigationDirection direction)
    {
        var to = stack.Front + (direction == FocusNavigationDirection.Left ? -1 : direction == FocusNavigationDirection.Right ? 1 : 0);
        if (to != stack.Front && ItemContainers.At(stack.Items, to) is { } container)
        {
            Visuals.Child<Button>(container)?.Focus();
        }
    }

    /// <summary>Gives the focus to the front card of the open category's stack, once its cards exist.</summary>
    public void FocusFrontCard()
    {
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_navigation.InFront || _navigation.MenuOpen || OpenStack is not { } stack || stack.Items.Items.Count == 0)
            {
                return;
            }

            var at = OpenKind is { } kind && ReferenceEquals(stack, ActionStack) ? _actionAt.GetValueOrDefault(kind) : stack.Front;
            at = Math.Clamp(at, 0, stack.Items.Items.Count - 1);
            if (ItemContainers.At(stack.Items, at) is { } container)
            {
                Visuals.Child<Button>(container)?.Focus();
            }

            // The card that already had the focus raises no focus event.
            ShowCard(stack, at);
            if (OpenKind != CategoryKind.Games)
            {
                _backdrop.ShowGreen();
            }
        });
    }

    // ===== NotInstalledCard =====

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

    // Where the small card was when it was opened, which the big one grows from. Whether the page is laid
    // out inside the open card, and whether it is growing, are kept by LibraryNavigation.
    private Rect? _cardFrom;

    // Bumped whenever the card starts to grow or shrink, so an older move does not finish over a newer.
    private int _cardMove;

    /// <summary>
    /// Opens the Not installed card. It grows from its place among the cards at once, and the games are
    /// only made when it has: there may be hundreds, and making them holds everything up for a moment,
    /// which is better spent looking at the open card than at a press that seems to have done nothing.
    /// </summary>
    private void BrowseNotInstalled()
    {
        if (_navigation.CardMoving)
        {
            return;
        }

        _cardFrom = FrontCardPlace();
        _parts.OpenCard.Visibility = Visibility.Visible;
        if (_cardFrom is not { } from || CardAt(from) is not { } at)
        {
            ShowNotInstalled();
            return;
        }

        // Nothing is pressed or moved to on the way, as the cards underneath are on their way out.
        _navigation.BeginCardMove();
        _parts.Scroller.IsHitTestVisible = false;

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
        _parts.Scroller.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, CardFade));
    }

    /// <summary>Fills the open card with the games that are not installed, which fade in once they have been drawn.</summary>
    private void ShowNotInstalled()
    {
        // Another window came in front while the card grew: it would only be put away again.
        if (!_window.IsActive)
        {
            SettleCard();
            return;
        }

        _filterBeforeBrowsing = _state.Filter;
        _state.Filter = TileFilter.Uninstalled;
        _host.ShowTiles();
        if (!_navigation.CardOpen)
        {
            // The last of them was installed or hidden meanwhile, and there is nothing to open.
            SettleCard();
            return;
        }

        _host.RestoreFocus();
        _parts.Scroller.Opacity = _parts.OpenCardHeading.Opacity = 0;

        var turn = ++_cardMove;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
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

            _parts.OpenCardHeading.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, CardFade));
            _parts.Scroller.BeginAnimation(UIElement.OpacityProperty, fade);
        });
    }

    /// <summary>Back from the open Not installed card to the cards, with the focus on the card it grew from.</summary>
    public bool LeaveNotInstalled()
    {
        if (!_layouts.IsBrowsing)
        {
            return false;
        }

        _state.Filter = _filterBeforeBrowsing;
        GameCards.Fresh = true;
        _host.ShowTiles();

        var at = _categories.FindIndex(c => c.Kind == CategoryKind.NotInstalled);
        if (at >= 0)
        {
            _category = at;
            MoveCategoryList(at, animate: false);
            ShowCategoryCards();
            _updateDeals();
            QueueCardReset();
        }

        _host.RestoreFocus(leavingTopBar: true);
        ShrinkCard();
        return true;
    }

    /// <summary>
    /// Lays the page out inside the open card, or as it is without one, when which of the two it should
    /// be has changed: the card was opened or closed, or the layout changed under it.
    /// </summary>
    private void ShowOpenCard()
    {
        var open = _layouts.IsBrowsing;
        if (open == _navigation.CardOpen)
        {
            return;
        }

        _navigation.SetCardOpen(open);
        SettleCard();

        _parts.OpenCardHeading.Visibility = _parts.BackHint.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _parts.Scroller.Margin = open ? OpenCardPage : default;
        MoveSearch(open ? _parts.OpenCardSearch : _parts.TopBar.TopBarCentre);
    }

    /// <summary>How many games the open card holds, or how many of them a search has found.</summary>
    public void UpdateOpenCardCount()
    {
        if (!_layouts.IsBrowsing)
        {
            return;
        }

        var owned = _state.Shown(_options.ShowHidden).Count(t => !t.IsInstalled);
        _parts.OpenCardCount.Text = _state.Tiles.Count != owned ? $"{_state.Tiles.Count} of {owned} games"
            : owned == 1 ? "1 game"
            : $"{owned} games";
    }

    /// <summary>
    /// Moves the search button, its bar and its store button between the top bar and the open card's
    /// heading. The Old School layout has no top bar, and these games are the only ones searched.
    /// </summary>
    private void MoveSearch(Panel to)
    {
        if (ReferenceEquals(_parts.TopBar.SearchButton.Parent, to))
        {
            return;
        }

        FrameworkElement[] parts = [_parts.TopBar.StoreButton, _parts.TopBar.SearchPanel, _parts.TopBar.SearchButton];
        foreach (var part in parts)
        {
            (part.Parent as Panel)?.Children.Remove(part);
        }

        // In the top bar they come straight after the tabs.
        var at = ReferenceEquals(to, _parts.TopBar.TopBarCentre) ? _parts.TopBar.TopBarCentre.Children.IndexOf(_parts.TopBar.FilterStrip) + 1 : 0;
        for (var i = 0; i < parts.Length; i++)
        {
            to.Children.Insert(at + i, parts[i]);
        }
    }

    /// <summary>Where the card at the front of the green cards is drawn, which the open card grows from and shrinks to.</summary>
    private Rect? FrontCardPlace() =>
        ActionStack.Panel is { IsVisible: true } panel
            ? panel.TransformToVisual(_parts.ScaledContent).TransformBounds(panel.FrontCard)
            : null;

    /// <summary>
    /// Shrinks the card back to the small one it was, which is among the cards again by now and fades
    /// in over it with the rest.
    /// </summary>
    private void ShrinkCard()
    {
        // Put away behind another window, where there is no one to watch it shrink.
        if (!_window.IsActive)
        {
            SettleCard();
            return;
        }

        // The cards are laid out first, to see where the small one is.
        _parts.ScaledContent.UpdateLayout();
        if ((FrontCardPlace() ?? _cardFrom) is not { } to)
        {
            return;
        }

        _parts.OpenCard.Visibility = Visibility.Visible;
        if (CardAt(to) is not { } at)
        {
            SettleCard();
            return;
        }

        var (scale, move, small) = at;
        _parts.Scroller.Opacity = 0;
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

        _parts.Scroller.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// Gives the open card's back the transforms that move it, at rest to begin with, and works out the
    /// ones that would draw it over a small card: how much smaller, and how far from its own corner.
    /// </summary>
    private (ScaleTransform Scale, TranslateTransform Move, (double ScaleX, double ScaleY, double X, double Y) Small)? CardAt(Rect small)
    {
        // Measured at rest, as its place is worked out with its own transform in it.
        _parts.OpenCardBack.RenderTransform = Transform.Identity;
        _parts.ScaledContent.UpdateLayout();

        var full = new Rect(_parts.OpenCardBack.TranslatePoint(default, _parts.ScaledContent), _parts.OpenCardBack.RenderSize);
        if (full.Width <= 0 || full.Height <= 0 || small.Width <= 0 || small.Height <= 0)
        {
            return null;
        }

        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        _parts.OpenCardBack.RenderTransform = new TransformGroup { Children = { scale, move } };
        return (scale, move, (small.Width / full.Width, small.Height / full.Height, small.X - full.X, small.Y - full.Y));
    }

    /// <summary>Ends a move that is part way: the card is fully open or gone, and the page fully there.</summary>
    private void SettleCard()
    {
        _cardMove++;
        _navigation.EndCardMove();
        _parts.Scroller.IsHitTestVisible = true;
        _parts.OpenCardBack.RenderTransform = Transform.Identity;
        _parts.OpenCard.Visibility = _navigation.CardOpen ? Visibility.Visible : Visibility.Collapsed;

        foreach (var faded in new UIElement[] { _parts.Scroller, _parts.OpenCardHeading })
        {
            faded.BeginAnimation(UIElement.OpacityProperty, null);
            faded.Opacity = 1;
        }
    }
}
