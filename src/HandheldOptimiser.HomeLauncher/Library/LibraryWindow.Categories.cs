using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Old School layout's categories: a list of names above the cards, as the Xbox 360's channels
/// were, in place of the tabs and buttons along the top. Up and down move between them, and the cards
/// below are the open one's: the games of a tab, the deals, or cards for the settings and for power.
/// </summary>
public partial class LibraryWindow
{
    private enum CategoryKind
    {
        Games,
        NotInstalled,
        Deals,
        Settings,
        Power
    }

    /// <summary>One name in the list. A tab of games carries the filter that shows them.</summary>
    private sealed record Category(string Name, CategoryKind Kind, TileFilter Filter = default);

    private static readonly Duration CategoryMove = TimeSpan.FromMilliseconds(240);

    // The green of the Xbox 360's own cards, behind the categories that have no game to take colours from.
    private static readonly Color CategoryGreen = Color.FromRgb(0x86, 0xB5, 0x1C);
    private static readonly Color CategoryDeepGreen = Color.FromRgb(0x24, 0x4A, 0x08);

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

    private CardStack ActionStack => _actionCards ??= new CardStack(ActionCards);

    /// <summary>The kind of category that is open, while the layout is Old School.</summary>
    private CategoryKind? OpenKind => IsOldSchool && _category < _categories.Count ? _categories[_category].Kind : null;

    /// <summary>The stack of cards the open category shows.</summary>
    private CardStack? OpenStack => OpenKind switch
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
        if (DealsWanted && _deals is { Count: > 0 })
        {
            categories.Add(new Category("Deals", CategoryKind.Deals));
        }

        categories.AddRange(FilterOptions()
            .Select(f => f.NotInstalled ? new Category(f.Name, CategoryKind.NotInstalled, f) : new Category(f.Name, CategoryKind.Games, f)));

        categories.Add(new Category("Settings", CategoryKind.Settings));
        categories.Add(new Category("Power", CategoryKind.Power));
        return categories;
    }

    /// <summary>
    /// Makes the list of categories match the library, after the games or the deals changed, keeping the
    /// open one open, and shows the Old School view or puts it away for the layout in use.
    /// </summary>
    private void RefreshCategories()
    {
        if (_layout != LibraryLayout.OldSchool)
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
                at = Math.Max(0, categories.FindIndex(c => c.Kind == CategoryKind.Games && c.Filter == _filter));
            }

            _categories = categories;
            _category = at;
            Categories.ItemsSource = categories;
            MoveCategoryList(at, animate: false);
        }

        ShowCategoryCards();
    }

    /// <summary>Opens the category for the tab the games are on, after the tab was changed some other way.</summary>
    private void OpenCategoryOfFilter()
    {
        var at = _categories.FindIndex(c => c.Kind is CategoryKind.Games or CategoryKind.NotInstalled && c.Filter == _filter);
        if (_layout == LibraryLayout.OldSchool && at >= 0 && at != _category)
        {
            _category = at;
            MoveCategoryList(at, animate: false);
        }
    }

    /// <summary>Moves the list so a category is the open one, by the shorter way round.</summary>
    private void MoveCategoryList(int to, bool animate)
    {
        if (VisualChild<OldSchoolListPanel>(Categories) is not { } panel)
        {
            // Not laid out yet: the list is put in place once it is.
            _categoryPosition = to;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (VisualChild<OldSchoolListPanel>(Categories) is { } made)
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
    private void StepCategory(int step)
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
        if (index < 0 || index >= _categories.Count || _menu is not null)
        {
            return;
        }

        DisarmCard();
        _category = index;
        MoveCategoryList(index, animate: true);

        var category = _categories[index];
        Notice = string.Empty;
        if (category.Kind == CategoryKind.Games && category.Filter != _filter)
        {
            _filter = category.Filter;
            GameCards.Fresh = true;
            ApplyFilter();
        }
        else
        {
            // Its cards slide in, as another tab's games do.
            if (OpenStack is { } stack)
            {
                stack.Fresh = true;
            }

            ShowCategoryCards();
            UpdateDeals();
            UpdateEmptyText();
            QueueCardReset();
        }

        RestoreFocus(leavingTopBar: true);
    }

    /// <summary>Shows the open category's stack of cards and puts the others away, or the whole view when the layout is another.</summary>
    private void ShowCategoryCards()
    {
        var oldSchool = IsOldSchool;
        var kind = OpenKind;

        // The names take the place of the tabs and buttons along the top, which keep their room so the clock stays put.
        // That goes for the open Not installed card as well, which has the search in its own heading.
        TopBarCentre.Visibility = _layout == LibraryLayout.OldSchool ? Visibility.Hidden : Visibility.Visible;
        Categories.Visibility = oldSchool ? Visibility.Visible : Visibility.Collapsed;
        ShowOpenCard();
        FitCategories();

        // The cards sit at the bottom of the screen, with the names above them. Nothing is left to scroll,
        // so a finger moving up or down is free to move between the categories.
        Page.VerticalAlignment = oldSchool ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Scroller.PanningMode = oldSchool ? PanningMode.None : PanningMode.VerticalOnly;
        Scroller.Padding = IsBrowsing ? OpenCardEdges : oldSchool ? CardEdges : PageEdges;

        var games = !oldSchool || kind == CategoryKind.Games;
        TilesScroller.Visibility = games ? Visibility.Visible : Visibility.Collapsed;

        // The quick actions are a game's, and there is nothing for X to open on the other cards.
        OptionsHint.Visibility = _quickActions && games ? Visibility.Visible : Visibility.Collapsed;

        var actions = kind is CategoryKind.NotInstalled or CategoryKind.Settings or CategoryKind.Power;
        ActionCards.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        if (actions)
        {
            var cards = ActionsOf(kind!.Value);
            if (ActionCards.ItemsSource is not List<ActionCard> shown || !shown.Select(c => c.Name).SequenceEqual(cards.Select(c => c.Name)))
            {
                ActionCards.ItemsSource = cards;
            }
        }
        else
        {
            ActionCards.ItemsSource = null;
        }
    }

    /// <summary>Draws the names as much bigger as the cards are drawn to fill a wide screen, so the two keep their proportions.</summary>
    private void FitCategories()
    {
        if (!IsOldSchool || Scroller.ViewportWidth <= 0)
        {
            return;
        }

        var fit = OldSchoolStack.Fit(Scroller.ViewportWidth - Tiles.Margin.Left - Tiles.Margin.Right);
        if (Categories.LayoutTransform is not ScaleTransform scale || Math.Abs(scale.ScaleX - fit) > 0.005)
        {
            Categories.LayoutTransform = new ScaleTransform(fit, fit);
        }
    }

    // ----- The cards of the categories that are not games or deals -----

    private List<ActionCard> ActionsOf(CategoryKind kind)
    {
        var cards = new List<ActionCard>();
        switch (kind)
        {
            case CategoryKind.NotInstalled:
                var owned = ShownTiles().Count(t => !t.IsInstalled);
                cards.Add(new ActionCard("", "Not installed",
                    owned == 1 ? "1 game you own, ready to install" : $"{owned} games you own, ready to install", BrowseNotInstalled));
                break;

            case CategoryKind.Settings:
                if (_update is { } update)
                {
                    cards.Add(new ActionCard("", "Update now", $"Handheld Optimiser {Updates.Display(update)} is available",
                        () => OnUpdateNow(this, new RoutedEventArgs())));
                }

                cards.Add(new ActionCard("", LibraryDisplay, null,
                    () => OpenSettingsCategoryFromCard(LibraryDisplayItems)));
                cards.Add(new ActionCard("", GamesApps, null,
                    () => OpenSettingsCategoryFromCard(GamesAppsItems)));
                cards.Add(new ActionCard("", SleepTweaks, null,
                    () => OpenSettingsCategoryFromCard(SleepTweaksItems)));
                cards.Add(new ActionCard("", "Refresh library", null,
                    () => OnRescan(this, new RoutedEventArgs())));
                cards.Add(new ActionCard("", "Close library", null, () => OnQuit(this, new RoutedEventArgs())));
                break;

            case CategoryKind.Power:
                cards.Add(new ActionCard("", "Sleep", null, () => OnSleep(this, new RoutedEventArgs())));
                if (Power.CanHibernate)
                {
                    cards.Add(new ActionCard("", "Hibernate", null, () => OnHibernate(this, new RoutedEventArgs())));
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

    /// <summary>One of the settings' three lists, opened from its card, which turns over to show it. Back turns it over again.</summary>
    private void OpenSettingsCategoryFromCard(StackPanel category)
    {
        OpenSettings();
        OpenSettingsCategory(category);
        _menuFromCard = true;
    }

    /// <summary>
    /// Opens the settings or power category, for the Menu and View buttons: in the Old School layout
    /// those are cards like any others, in place of a menu over the library.
    /// </summary>
    private void OpenCategoryOf(CategoryKind kind)
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

        // A paused game could not close as Windows asks it to, and would hold up the restart.
        if (_session is { } session)
        {
            ResumeGame(session);
        }

        ReportPower(action());
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

        var index = (ActionCards.ItemsSource as List<ActionCard>)?.IndexOf(card) ?? -1;
        _actionAt[kind] = index;
        AcceptHint.Text = "Select";
        CardFocused(ActionStack, card, index);
        ShowCategoryBackdrop();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        // A tap on a card behind the front one only brings it forward.
        if (sender is Button { DataContext: ActionCard card } && !CardClickSwallowed(ActionStack, card))
        {
            card.Act();
        }
    }

    private void OnCategoryPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Category category })
        {
            OpenCategory(_categories.IndexOf(category));
            e.Handled = true;
        }
    }

    /// <summary>Left and right along a stack of cards that has the focus.</summary>
    private static void NavigateCards(CardStack stack, FocusNavigationDirection direction)
    {
        var to = stack.Front + (direction == FocusNavigationDirection.Left ? -1 : direction == FocusNavigationDirection.Right ? 1 : 0);
        if (to != stack.Front && ItemContainers.At(stack.Items, to) is { } container)
        {
            VisualChild<Button>(container)?.Focus();
        }
    }

    /// <summary>Gives the focus to the front card of the open category's stack, once its cards exist.</summary>
    private void FocusFrontCard()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsActive || _menu is not null || OpenStack is not { } stack || stack.Items.Items.Count == 0)
            {
                return;
            }

            var at = OpenKind is { } kind && ReferenceEquals(stack, ActionStack) ? _actionAt.GetValueOrDefault(kind) : stack.Front;
            at = Math.Clamp(at, 0, stack.Items.Items.Count - 1);
            if (ItemContainers.At(stack.Items, at) is { } container)
            {
                VisualChild<Button>(container)?.Focus();
            }

            // The card that already had the focus raises no focus event.
            ShowCard(stack, at);
            if (OpenKind != CategoryKind.Games)
            {
                ShowCategoryBackdrop();
            }
        });
    }

    /// <summary>The Xbox 360's green behind the categories that have no game to take colours from.</summary>
    private async void ShowCategoryBackdrop()
    {
        const string key = "category:green";
        if (_backdropKey == key || !IsActive || _background == BackgroundKind.Plain)
        {
            return;
        }

        _backdropKey = key;
        _backdropLoad?.Cancel();
        var load = _backdropLoad = new CancellationTokenSource();

        var (width, height) = BackdropPixels();
        var shade = _strength switch
        {
            ColourStrength.Subtle => 1,
            ColourStrength.Strong => 0.55,
            _ => 0.7
        };

        var backdrop = await Task.Run(() => Backdrops.Gradient(CategoryGreen, CategoryDeepGreen, width, height, shade));
        if (!load.IsCancellationRequested)
        {
            ShowBackdrop(backdrop);
        }
    }
}

/// <summary>
/// A card that does something when it is pressed: one of the settings' lists, a power action, or the
/// way in to the games that are not installed. Green, with a white symbol, as the Xbox 360's own were.
/// </summary>
internal sealed class ActionCard(string glyph, string name, string? detail, Action act) : INotifyPropertyChanged, INoticeHost
{
    private string _title = name;
    private string? _notice;

    public event PropertyChangedEventHandler? PropertyChanged;

    // The symbols' shapes, made once each: the cards are made again whenever their category is opened.
    private static readonly Dictionary<string, Geometry> Shapes = [];

    /// <summary>A symbol from Windows' icon font.</summary>
    public string Glyph { get; } = glyph;

    /// <summary>
    /// The symbol as a shape, which is how the card draws it. As text it went missing from the cards
    /// furthest back: text that big is not drawn at all once its card is made small enough, where a
    /// shape is drawn at any size.
    /// </summary>
    public Geometry Icon => Shapes.TryGetValue(Glyph, out var shape) ? shape : Shapes[Glyph] = Outline(Glyph);

    private static Geometry Outline(string glyph)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 100, Brushes.White, 1);
        var shape = text.BuildGeometry(default);
        shape.Freeze();
        return shape;
    }

    /// <summary>What the card is, which is also what it says unless it is waiting for a second press.</summary>
    public string Name { get; } = name;

    public string? Detail { get; } = detail;

    public bool HasDetail => Detail is not null;

    public Action Act { get; } = act;

    /// <summary>What the library has to say, shown on the card in place of its name and detail.</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            if (_notice != value)
            {
                _notice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notice)));
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }
    }
}
