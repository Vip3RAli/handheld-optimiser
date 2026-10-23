using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The menu: one box whose lists take each other's place, and the text box for typed settings. It brings
/// the box on screen and takes it away, moves between the lists, goes Back, and steps a row's setting with
/// left and right. What each list's rows do is the business of the menu that owns them, which registers
/// where its lists go Back to and how its rows step.
///
/// In the grid and the row the box is a panel over the dimmed library. In the Old School layout the card
/// that was pressed turns over and the menu is its other side: in the card's own place, at the card's own
/// size and in its colours, so the cards are never left for another screen.
/// </summary>
internal sealed class MenuPresenter
{
    // A card turns edge on, and its other side turns to face the player.
    private static readonly Duration TurnAway = TimeSpan.FromMilliseconds(130);
    private static readonly Duration TurnToFace = TimeSpan.FromMilliseconds(170);

    // The menu as MenuView.xaml lays it out over the dimmed library.
    private const double MenuWidth = 560;
    private const double MenuTitleSize = 22;
    private static readonly Thickness MenuMargin = new(0, 24, 0, 24);
    private static readonly Thickness MenuPadding = new(28, 24, 28, 20);
    private static readonly Thickness MenuStoreMargin = new(0, 2, 0, 18);
    private static readonly Brush MenuDim = LibraryBrushes.Frozen("#C0000000");
    private static readonly Brush MenuBack = LibraryBrushes.Frozen("#181B22");
    private static readonly Brush MenuText = LibraryBrushes.Frozen("#F2F3F5");

    // The menu on the back of a card, which has a good deal less room: tighter edges and a smaller
    // heading, and rows the card's colours show through, with the one in focus picked out in white.
    private const double CardTitleSize = 19;
    private static readonly Thickness CardPadding = new(14, 12, 14, 6);
    private static readonly Thickness CardStoreMargin = new(0, 0, 0, 10);
    private static readonly Brush CardRow = LibraryBrushes.Frozen("#38000000");
    private static readonly Brush CardRowFocus = LibraryBrushes.Frozen("#40FFFFFF");
    private static readonly Brush CardBack = LibraryBrushes.Frozen("#262B36");

    // Boxed once: the rows look them up as the resources they are in LibraryResources.xaml.
    private static readonly object CardRowPadding = new Thickness(14, 6, 14, 6);
    private static readonly object CardRowMargin = new Thickness(0, 0, 0, 6);
    private static readonly object CardRowHeight = 42d;

    // How much of a game's own colours the back of its card keeps: darkened, so white text reads on it.
    private const double CardBackShade = 0.62;

    private readonly MenuView _view;
    private readonly LibraryNavigation _navigation;
    private readonly NoticePresenter _notices;
    private readonly Func<(OldSchoolPanel Panel, Button Card)?> _cardToTurn;
    private readonly Func<bool> _overOpenCard;
    private readonly Visual _page;
    private readonly Action _restoreFocus;

    // Where each list goes Back to, and the menus that step a row's setting with left and right.
    private readonly Dictionary<StackPanel, Func<bool>> _backs = [];
    private readonly List<Func<Button, int, bool>> _steppers = [];

    // Rows added to a list when it opens, removed again before it is filled the next time.
    private readonly Dictionary<StackPanel, List<Button>> _addedRows = [];

    // What the text box saves to, while it is open.
    private Action<string>? _save;

    // The card that has turned over to show the menu, and the one on its way back.
    private Button? _turned;
    private Button? _turningBack;

    // Bumped whenever a turn begins, so the second half of an older one does not finish over a newer.
    private int _turn;

    /// <param name="cardToTurn">
    /// In the Old School layout, the open category's panel and the card at its front, which turns over to
    /// show the menu. Null in the other layouts, where the menu is over the dimmed library.
    /// </param>
    /// <param name="overOpenCard">Whether the open Not installed card is under the menu, which it takes the green of.</param>
    /// <param name="page">What the cards are drawn in, which the menu is placed over.</param>
    /// <param name="restoreFocus">Puts the focus back on the page, as the menu closes.</param>
    public MenuPresenter(MenuView view, LibraryNavigation navigation, NoticePresenter notices,
        Func<(OldSchoolPanel Panel, Button Card)?> cardToTurn, Func<bool> overOpenCard, Visual page, Action restoreFocus)
    {
        _view = view;
        _navigation = navigation;
        _notices = notices;
        _cardToTurn = cardToTurn;
        _overOpenCard = overOpenCard;
        _page = page;
        _restoreFocus = restoreFocus;

        // A press outside the box closes it.
        view.MenuOverlay.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, view.MenuOverlay))
            {
                Close();
            }
        };

        view.SaveEditItem.Click += (_, _) => SaveEdit();
        view.CancelEditItem.Click += (_, _) => ShowItems();
    }

    /// <summary>The list showing: a game's quick actions, one of the settings' lists, the power menu or quick settings. Null when closed.</summary>
    public StackPanel? List { get; private set; }

    /// <summary>The game the quick actions are for, or null for a menu that is not about a game.</summary>
    public GameTile? Tile { get; private set; }

    /// <summary>
    /// Whether the list was opened from its own Old School card, with no settings list behind it: Back
    /// turns the card over again.
    /// </summary>
    public bool FromCard { get; set; }

    /// <summary>The row that has been pressed once and acts on the next press, as Restart and the clean-ups do.</summary>
    public Button? Armed { get; set; }

    /// <summary>Whether the menu is on the back of an Old School card, and so short of room.</summary>
    public bool OnCard => _turned is not null;

    /// <summary>The heading at the top of the box.</summary>
    public string Title
    {
        set => _view.MenuTitle.Text = value;
    }

    /// <summary>The line under the heading: the list it was opened from, or what is going on in it.</summary>
    public string Subtitle
    {
        set => _view.MenuStore.Text = value;
    }

    /// <summary>Says where a list goes Back to. The function returns false when it has nowhere to go, and the menu closes.</summary>
    public void SetBack(StackPanel list, Func<bool> back) => _backs[list] = back;

    /// <summary>
    /// Adds a menu that steps its rows' settings with left and right. It is asked about the focused row, and
    /// says false for a row that is not its own.
    /// </summary>
    public void AddStepper(Func<Button, int, bool> step) => _steppers.Add(step);

    /// <summary>Whether a row is in the list showing.</summary>
    public bool Contains(Button row) => List?.Children.Contains(row) == true;

    /// <summary>
    /// Opens the menu on a list, over the library or on the back of the front card. The rows should be
    /// filled in first, as the focus goes to the first one that can be pressed.
    /// </summary>
    /// <param name="tile">The game it is about, for a game's quick actions.</param>
    /// <returns>False when the menu cannot open from where the player is.</returns>
    public bool Open(StackPanel list, string title, string subtitle, GameTile? tile = null)
    {
        if (!_navigation.OpenMenu())
        {
            return false;
        }

        Tile = tile;
        Switch(list, title, subtitle);
        ShowOverlay();
        ShowItems();
        return true;
    }

    /// <summary>Puts another list in place of the one showing, with the focus on a row of it or its first.</summary>
    /// <param name="title">The heading, or null to keep the one showing, as a game's own lists keep its name.</param>
    public void Show(StackPanel list, string? title, string subtitle, Button? focus = null)
    {
        Switch(list, title, subtitle);
        ShowItems(focus);
    }

    private void Switch(StackPanel list, string? title, string subtitle)
    {
        HideLists();
        List = list;
        if (title is not null)
        {
            _view.MenuTitle.Text = title;
        }

        _view.MenuStore.Text = subtitle;
    }

    /// <summary>Every list the box can show, collapsed, before one of them is opened.</summary>
    private void HideLists()
    {
        var view = _view;
        foreach (var list in new[]
        {
            view.GameItems, view.ProfileItems, view.RecommendedItems, view.SettingsItems, view.LibraryDisplayItems,
            view.GamesAppsItems, view.SleepTweaksItems, view.BackgroundItems, view.DisplayItems, view.AddItems, view.PowerItems,
            view.QuickItems, view.WhilePlayingItems, view.AppsToCloseItems, view.RunningAppsItems, view.MaintenanceItems,
            view.StartupItems, view.EmulatorItems, view.TvItems, view.OwnedItems, view.DealsItems
        })
        {
            list.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The list showing, which is also where cancelling the text box goes back to.</summary>
    public void ShowItems(Button? focus = null)
    {
        if (List is not { } list)
        {
            return;
        }

        _save = null;
        _navigation.EndEdit();
        _view.TextEditor.Visibility = Visibility.Collapsed;
        list.Visibility = Visibility.Visible;
        ShowHints(list);

        // From the top, with its heading: the last list may have been left scrolled, and the row that
        // takes the focus is brought back into view if it is further down.
        _view.MenuScroller.ScrollToVerticalOffset(0);

        // Wait for the rows to be laid out before one can take focus.
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => (focus ?? List?.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled && b.IsVisible))?.Focus());
    }

    public void Close()
    {
        if (List is null)
        {
            return;
        }

        List = null;
        Tile = null;
        _save = null;
        Armed = null;
        _navigation.CloseMenu();
        HideOverlay();
        _notices.Place();
        _restoreFocus();
    }

    /// <summary>B, Escape or a Back row: one list back, or the menu closes when there is nowhere to go back to.</summary>
    public void Back()
    {
        if (List is not { } list || !_backs.TryGetValue(list, out var back) || !back())
        {
            Close();
        }
    }

    /// <summary>Up and down move between the rows that can be pressed. Left and right step the focused row's setting.</summary>
    public void MoveFocus(FocusNavigationDirection direction)
    {
        if (direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
        {
            if (Keyboard.FocusedElement is Button row)
            {
                var step = direction == FocusNavigationDirection.Right ? 1 : -1;
                _steppers.Any(stepper => stepper(row, step));
            }

            return;
        }

        var move = direction switch
        {
            FocusNavigationDirection.Up => -1,
            FocusNavigationDirection.Down => 1,
            _ => 0
        };

        var items = List?.Children.OfType<Button>().Where(b => b.IsEnabled && b.IsVisible).ToList() ?? [];
        if (move == 0 || items.Count == 0)
        {
            return;
        }

        var index = Keyboard.FocusedElement is Button focused ? items.IndexOf(focused) : -1;
        items[Math.Clamp(index + move, 0, items.Count - 1)].Focus();
    }

    // ----- Rows a list is filled with as it opens -----

    /// <summary>Takes away the rows a list was filled with last time.</summary>
    public void ClearRows(StackPanel list)
    {
        foreach (var row in _addedRows.GetValueOrDefault(list) ?? [])
        {
            list.Children.Remove(row);
        }

        _addedRows[list] = [];
    }

    /// <summary>Adds a row to a list, above the given row, with what it is set to when it is a setting.</summary>
    public Button AddRow(StackPanel list, UIElement before, string content, string? value, Action click)
    {
        var row = new Button
        {
            Style = (Style)_view.FindResource("MenuButton"),
            Content = content,
            Tag = value
        };

        row.Click += (_, _) => click();
        list.Children.Insert(list.Children.IndexOf(before), row);
        (_addedRows.TryGetValue(list, out var rows) ? rows : _addedRows[list] = []).Add(row);
        return row;
    }

    // ----- The text box -----

    /// <summary>Opens the text box in place of the list, for a setting that is typed.</summary>
    /// <param name="save">Saves what was typed, trimmed. It also says where to go next.</param>
    public void BeginEdit(string title, string hint, string? text, Action<string> save)
    {
        if (List is not { } list || !_navigation.BeginEdit())
        {
            return;
        }

        _save = save;
        list.Visibility = Visibility.Collapsed;
        _view.TextEditor.Visibility = Visibility.Visible;

        _view.EditorTitle.Text = title;
        _view.EditorHint.Text = hint;
        _view.EditorBox.Text = text ?? string.Empty;
        _view.EditorBox.CaretIndex = _view.EditorBox.Text.Length;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _view.EditorBox.Focus());
    }

    /// <summary>Enter, Menu or the Save button: what was typed is saved.</summary>
    public void SaveEdit()
    {
        if (_save is { } save)
        {
            _save = null;
            save(_view.EditorBox.Text.Trim());
        }
    }

    // ----- How the box comes on screen -----

    /// <summary>Brings the box on screen: over the dimmed library, or as the other side of the front Old School card.</summary>
    private void ShowOverlay()
    {
        var view = _view;

        // Already up: its lists change in place.
        if (view.MenuOverlay.Visibility == Visibility.Visible && view.MenuOverlay.IsHitTestVisible)
        {
            return;
        }

        StopTurn();
        view.MenuOverlay.Visibility = Visibility.Visible;
        view.MenuOverlay.IsHitTestVisible = true;

        if (_cardToTurn() is var (panel, card))
        {
            TurnToMenu(panel, card);
            return;
        }

        var box = view.MenuBox;
        view.MenuOverlay.Background = MenuDim;
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Margin = MenuMargin;
        box.Padding = MenuPadding;
        box.Width = MenuWidth;
        box.Height = double.NaN;
        box.Background = MenuBack;
        box.LayoutTransform = Transform.Identity;
        box.RenderTransform = Transform.Identity;
        foreach (var key in new[] { "MenuRowBrush", "MenuRowFocusBrush", "MenuRowPadding", "MenuRowMargin", "MenuRowHeight" })
        {
            box.Resources.Remove(key);
        }

        TextElement.SetForeground(box, MenuText);
        view.MenuTitle.FontSize = MenuTitleSize;
        view.MenuStore.Margin = MenuStoreMargin;

        // Over the open Not installed card the menu is the size it is in the other layouts, as there is
        // no small card to turn over, and takes the card's green and its see-through rows.
        if (_overOpenCard())
        {
            box.Background = (Brush)view.FindResource("OldSchoolGreenBack");
            box.Resources["MenuRowBrush"] = CardRow;
            box.Resources["MenuRowFocusBrush"] = CardRowFocus;
            TextElement.SetForeground(box, Brushes.White);
        }
    }

    /// <summary>Takes the box off screen again. An Old School card that turned over to show it turns back.</summary>
    private void HideOverlay()
    {
        FromCard = false;
        var view = _view;
        if (_turned is not { } card || view.MenuBox.RenderTransform is not ScaleTransform { IsFrozen: false } menu)
        {
            StopTurn();
            view.MenuOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // The menu turns edge on and goes, and the card turns back to face the player. Nothing in it
        // can be pressed on the way.
        _turned = null;
        _turningBack = card;
        view.MenuOverlay.IsHitTestVisible = false;
        var turn = ++_turn;
        var away = new DoubleAnimation(0, TurnAway) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        away.Completed += (_, _) =>
        {
            if (turn != _turn)
            {
                return;
            }

            view.MenuOverlay.Visibility = Visibility.Collapsed;
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
        var view = _view;
        var box = view.MenuBox;
        var fit = panel.Fit;
        var front = panel.TransformToVisual(_page).TransformBounds(panel.FrontCard);

        // No dimming: the rest of the cards stay as they are around the one that turned. A press
        // outside the card still closes the menu.
        view.MenuOverlay.Background = Brushes.Transparent;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.VerticalAlignment = VerticalAlignment.Top;
        box.Margin = new Thickness(Math.Max(0, front.Left), Math.Max(0, front.Top), 0, 0);

        // Laid out at the card's own size and drawn as much bigger as the card is, as the card's face is.
        box.Width = OldSchoolStack.CardWidth;
        box.Height = panel.CardHeight;
        box.Padding = CardPadding;
        box.LayoutTransform = new ScaleTransform(fit, fit);

        box.Background = card.DataContext switch
        {
            ActionCard => (Brush)view.FindResource("OldSchoolGreenBack"),
            GameTile tile => GameBack(tile),
            _ => CardBack
        };

        box.Resources["MenuRowBrush"] = CardRow;
        box.Resources["MenuRowFocusBrush"] = CardRowFocus;
        box.Resources["MenuRowPadding"] = CardRowPadding;
        box.Resources["MenuRowMargin"] = CardRowMargin;
        box.Resources["MenuRowHeight"] = CardRowHeight;
        TextElement.SetForeground(box, Brushes.White);
        view.MenuTitle.FontSize = CardTitleSize;
        view.MenuStore.Margin = CardStoreMargin;

        // Both turn about their middle, which is the same place, so the one is seen to become the other.
        var menu = new ScaleTransform(0, 1);
        box.RenderTransformOrigin = new Point(0.5, 0.5);
        box.RenderTransform = menu;

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
    /// The button hints at the foot of a list, which the back of a card has no room for: the same buttons
    /// are along the bottom of the screen.
    /// </summary>
    private void ShowHints(StackPanel list)
    {
        foreach (var hints in list.Children.OfType<StackPanel>())
        {
            hints.Visibility = OnCard ? Visibility.Collapsed : Visibility.Visible;
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
        _view.MenuBox.RenderTransform = Transform.Identity;
    }
}
