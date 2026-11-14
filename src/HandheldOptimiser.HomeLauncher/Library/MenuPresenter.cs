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
/// In the grid and the row the box is a panel over the dimmed library. In the Old School layout, in either
/// of its looks, it is a settings panel after the Xbox 360's: slate, centred, its rows with icons and a
/// green bar on the one in focus, and a column beside the list saying what that row does.
/// </summary>
internal sealed class MenuPresenter
{
    // The menu as MenuView.xaml lays it out over the dimmed library.
    private const double MenuWidth = 560;
    private const double MenuTitleSize = 22;
    private static readonly Thickness MenuMargin = new(0, 24, 0, 24);
    private static readonly Thickness MenuPadding = new(28, 24, 28, 20);
    private static readonly Thickness MenuStoreMargin = new(0, 2, 0, 18);
    private static readonly CornerRadius MenuCorners = new(12);
    private static readonly Brush MenuDim = LibraryBrushes.Frozen("#C0000000");
    private static readonly Brush MenuBack = LibraryBrushes.Frozen("#181B22");
    private static readonly Brush MenuText = LibraryBrushes.Frozen("#F2F3F5");

    // The Old School layout's panel: slate with a light edge, over the cards only lightly dimmed.
    private static readonly Thickness PanelPadding = new(22, 18, 26, 18);
    private static readonly Thickness PanelDetailMargin = new(34, 6, 0, 0);
    private static readonly CornerRadius PanelCorners = new(5);
    private static readonly Brush PanelDim = LibraryBrushes.Frozen("#66000000");
    private static readonly Brush PanelEdge = LibraryBrushes.Frozen("#47FFFFFF");
    private static readonly Duration PanelFade = TimeSpan.FromMilliseconds(140);

    /// <summary>The share of the panel's width the column beside the list takes.</summary>
    internal const double PanelDetailShare = 0.4;

    private readonly MenuView _view;
    private readonly LibraryNavigation _navigation;
    private readonly NoticePresenter _notices;
    private readonly Func<bool> _oldSchool;
    private readonly Action _restoreFocus;

    // Where each list goes Back to, and the menus that step a row's setting with left and right.
    private readonly Dictionary<StackPanel, Func<bool>> _backs = [];
    private readonly List<Func<Button, int, bool>> _steppers = [];

    // Rows added to a list when it opens, removed again before it is filled the next time.
    private readonly Dictionary<StackPanel, List<Button>> _addedRows = [];

    // What the text box saves to, while it is open.
    private Action<string>? _save;

    // Whether the box is the Old School layout's panel.
    private bool _panel;

    /// <param name="oldSchool">Whether the Old School layout is on screen, where the menu is the panel.</param>
    /// <param name="restoreFocus">Puts the focus back on the page, as the menu closes.</param>
    public MenuPresenter(MenuView view, LibraryNavigation navigation, NoticePresenter notices, Func<bool> oldSchool, Action restoreFocus)
    {
        _view = view;
        _navigation = navigation;
        _notices = notices;
        _oldSchool = oldSchool;
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

        // The panel's column says what the row in focus does.
        view.MenuBox.GotKeyboardFocus += (_, e) =>
            view.MenuDetailText.Text = e.NewFocus is Button row ? MenuRow.GetAbout(row) : null;

        view.SizeChanged += (_, _) =>
        {
            if (_panel && List is not null)
            {
                ShowPanel();
            }
        };
    }

    /// <summary>The list showing: a game's quick actions, one of the settings' lists, the power menu or quick settings. Null when closed.</summary>
    public StackPanel? List { get; private set; }

    /// <summary>The game the quick actions are for, or null for a menu that is not about a game.</summary>
    public GameTile? Tile { get; private set; }

    /// <summary>
    /// Whether the list was opened from its own Old School card, with no settings list behind it: Back
    /// closes the menu.
    /// </summary>
    public bool FromCard { get; set; }

    /// <summary>The row that has been pressed once and acts on the next press, as Restart and the clean-ups do.</summary>
    public Button? Armed { get; set; }

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
    /// Opens the menu on a list, over the library or as the Old School panel. The rows should be filled in
    /// first, as the focus goes to the first one that can be pressed.
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
            view.StartupItems, view.EmulatorItems, view.TvItems, view.OwnedItems, view.DealsItems,
            view.AccountsItems, view.AchievementsItems, view.SaveSnapshotsItems, view.TagsItems
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

    /// <summary>Brings the box on screen: the panel in the Old School layout, or the box over the dimmed library.</summary>
    private void ShowOverlay()
    {
        // Already up: its lists change in place.
        if (_view.MenuOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        _view.MenuOverlay.Visibility = Visibility.Visible;
        Place(animate: true);
    }

    /// <summary>Takes the box off screen again.</summary>
    private void HideOverlay()
    {
        FromCard = false;
        _view.MenuOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// After the layout changes with the menu open, as Settings' own Layout row does: the box takes the
    /// shape of the new layout at once.
    /// </summary>
    public void Reshape()
    {
        if (List is { } list && _view.MenuOverlay.Visibility == Visibility.Visible && _panel != _oldSchool())
        {
            Place(animate: false);
            ShowHints(list);
        }
    }

    /// <summary>Gives the box its shape for where it is opened: the Old School panel, or the box over the dimmed library.</summary>
    private void Place(bool animate)
    {
        var view = _view;
        _panel = _oldSchool();
        view.MenuSheen.Visibility = _panel ? Visibility.Visible : Visibility.Collapsed;
        view.MenuDetail.Visibility = _panel ? Visibility.Visible : Visibility.Collapsed;
        if (!_panel)
        {
            ShowBox();
            return;
        }

        ShowPanel();
        if (animate)
        {
            view.MenuBox.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, PanelFade) { FillBehavior = FillBehavior.Stop });
        }
    }

    /// <summary>The box over the dimmed library, in the grid and the row.</summary>
    private void ShowBox()
    {
        var view = _view;
        var box = view.MenuBox;
        view.MenuOverlay.Background = MenuDim;
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Margin = MenuMargin;
        box.Padding = MenuPadding;
        box.Width = MenuWidth;
        box.Height = double.NaN;
        box.CornerRadius = MenuCorners;
        box.BorderThickness = default;
        box.Background = MenuBack;
        box.Resources.Remove("MenuRowTemplate");
        TextElement.SetForeground(box, MenuText);
        view.MenuTitle.FontSize = MenuTitleSize;
        view.MenuStore.Margin = MenuStoreMargin;
    }

    /// <summary>The Old School panel, sized to the screen and centred on it, with the panel's own rows.</summary>
    private void ShowPanel()
    {
        var view = _view;
        var box = view.MenuBox;
        var (width, height) = PanelSize(view.ActualWidth, view.ActualHeight);

        view.MenuOverlay.Background = PanelDim;
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.Margin = default;
        box.Width = width;
        box.Height = height;
        box.Padding = PanelPadding;
        box.CornerRadius = PanelCorners;
        box.BorderBrush = PanelEdge;
        box.BorderThickness = new Thickness(1);
        box.Background = (Brush)view.FindResource("MenuPanelBack");
        box.Resources["MenuRowTemplate"] = view.FindResource("MenuPanelRowTemplate");
        TextElement.SetForeground(box, MenuText);
        view.MenuTitle.FontSize = MenuTitleSize;
        view.MenuStore.Margin = MenuStoreMargin;

        // The sheen runs out to the panel's edges, over its padding.
        view.MenuSheen.Margin = new Thickness(-PanelPadding.Left, -PanelPadding.Top, -PanelPadding.Right, -PanelPadding.Bottom);
        view.MenuDetail.Width = Math.Round(width * PanelDetailShare);
        view.MenuDetail.Margin = PanelDetailMargin;
    }

    /// <summary>
    /// How big the Old School panel is on a screen of a given size: about two thirds of it each way, as the
    /// Xbox 360's was, within reason on a very small or very large screen, and never off its edges.
    /// </summary>
    internal static (double Width, double Height) PanelSize(double screenWidth, double screenHeight)
    {
        var width = Math.Min(Math.Clamp(screenWidth * 0.66, 760, 1180), screenWidth - 32);
        var height = Math.Min(Math.Clamp(screenHeight * 0.7, 440, 720), screenHeight - 32);
        return (Math.Max(0, Math.Round(width)), Math.Max(0, Math.Round(height)));
    }

    /// <summary>
    /// The button hints at the foot of a list. The Old School panel leaves them to the hints along the
    /// bottom of the screen, as the Xbox 360's did.
    /// </summary>
    private void ShowHints(StackPanel list)
    {
        foreach (var hints in list.Children.OfType<StackPanel>())
        {
            hints.Visibility = _panel ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
