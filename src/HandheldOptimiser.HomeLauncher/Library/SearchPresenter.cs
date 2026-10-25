using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Search on the Not installed tab, which can list hundreds of games: the top bar's search button opens
/// a box, and the games are narrowed down as each letter is typed. Beside the box, a button goes through
/// the stores to show one store's games. Menu or Enter goes to the games found, and View, Escape or B
/// among the games closes the search and shows them all again.
/// </summary>
internal sealed class SearchPresenter
{
    private readonly TopBarView _view;
    private readonly LibraryNavigation _navigation;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly Func<nint> _window;
    private readonly Action _showFound;
    private readonly Action _showAll;
    private readonly Action<bool> _restoreFocus;
    private readonly Func<object?, bool> _isTopBarButton;

    // A fast typist's letters are looked up together, so the grid is not rebuilt for each of them.
    private DispatcherTimer? _searchTimer;

    // Whether Windows' keyboard has been seen on screen since the typing began, and the timer that looks.
    private bool _keyboardSeen;
    private DispatcherTimer? _keyboardTimer;

    /// <param name="window">The window's handle, which Windows' keyboard is brought up for.</param>
    /// <param name="showFound">Shows the games the search now finds, from the top.</param>
    /// <param name="showAll">Shows the tab's games again, after the search is closed.</param>
    /// <param name="restoreFocus">Back to the games: true is the search asking to leave the top bar.</param>
    public SearchPresenter(TopBarView view, LibraryNavigation navigation, LibraryState state, LibraryOptions options,
        Func<nint> window, Action showFound, Action showAll, Action<bool> restoreFocus, Func<object?, bool> isTopBarButton)
    {
        _view = view;
        _navigation = navigation;
        _state = state;
        _options = options;
        _window = window;
        _showFound = showFound;
        _showAll = showAll;
        _restoreFocus = restoreFocus;
        _isTopBarButton = isTopBarButton;

        view.SearchButton.Click += (_, _) => Open();
        view.SearchBar.Click += (_, _) => BeginTyping();
        view.StoreButton.Click += (_, _) => StepStore();
        view.SearchBox.TextChanged += OnTextChanged;
        view.SearchBox.GotKeyboardFocus += OnFocusChanged;
        view.SearchBox.LostKeyboardFocus += OnFocusChanged;
    }

    /// <summary>What the games are narrowed down to now. Empty shows them all.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>The one store whose games are shown, or null for every store's.</summary>
    public GameStore? Store { get; private set; }

    /// <summary>Whether the box and the store button are showing, until the search is closed.</summary>
    public bool IsOpen => _view.SearchPanel.Visibility == Visibility.Visible;

    /// <summary>Set while a press of A is acted on, which arrives as a click like a tap or the mouse does.</summary>
    public bool PressedByController { get; set; }

    /// <summary>Whether the search, with its store, leaves the game in.</summary>
    public bool Found(GameTile tile) =>
        (Store is not { } store || tile.Game.Store == store)
        && (Text.Length == 0 || GameSearch.Matches(tile.Title, Text));

    /// <summary>
    /// The search button. From the controller or the keyboard it opens the search with the bar picked
    /// out, to type in with A or to leave for the store button. A tap or a click goes straight to typing.
    /// </summary>
    private void Open()
    {
        Show(true);
        if (PressedByController || InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            _view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _view.SearchBar.Focus());
        }
        else
        {
            BeginTyping();
        }
    }

    /// <summary>Puts the cursor in the bar, which brings Windows' keyboard up.</summary>
    private void BeginTyping()
    {
        Show(true);
        _view.SearchBar.Visibility = Visibility.Hidden;
        _view.SearchEntry.Visibility = Visibility.Visible;
        _view.SearchBox.CaretIndex = _view.SearchBox.Text.Length;
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _view.SearchBox.Focus());
    }

    private void Show(bool show)
    {
        _view.SearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _view.StoreButton.Visibility = _view.SearchPanel.Visibility;
        _view.StoreButton.Content = Store is { } store ? Game.NameOf(store) : "All stores";
        ShowSearchText();
    }

    // What was searched for on the bar while it is not typed in, or what the bar is for.
    private void ShowSearchText()
    {
        var text = _view.SearchBox.Text.Trim();
        _view.SearchText.Text = text.Length > 0 ? text : "Search";
        _view.SearchText.Opacity = text.Length > 0 ? 1 : 0.6;
    }

    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Typing follows the box's own keyboard focus, so where the library thinks the player is cannot
        // drift from what WPF thinks.
        if (_view.SearchBox.IsKeyboardFocused)
        {
            _navigation.BeginTyping();
        }
        else
        {
            _navigation.EndTyping();
        }

        UpdateHint();

        // Windows does not bring its keyboard up for a box the controller focused, or with one plugged in.
        var window = _window();
        if (_navigation.Typing)
        {
            _keyboardSeen = false;
            TouchKeyboard.Show(window);
            _keyboardTimer ??= NewKeyboardTimer();
            _keyboardTimer.Start();
            return;
        }

        // The typing is over: the bar shows what was typed, and the d-pad can pick it out again.
        _keyboardTimer?.Stop();
        TouchKeyboard.Hide(window);
        _view.SearchEntry.Visibility = Visibility.Collapsed;
        _view.SearchBar.Visibility = Visibility.Visible;
        ShowSearchText();
        _searchTimer?.Stop();
        Apply();
    }

    /// <summary>
    /// Watches for Windows' keyboard being put away, as B does on it, which leaves the cursor in the bar
    /// with nothing to type on. The typing ends then, with the bar picked out for the d-pad.
    /// </summary>
    private DispatcherTimer NewKeyboardTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (!_navigation.Typing)
            {
                timer.Stop();
            }
            else if (TouchKeyboard.IsShowing())
            {
                _keyboardSeen = true;
            }
            else if (_keyboardSeen)
            {
                timer.Stop();
                EndTyping();
            }
        };
        return timer;
    }

    /// <summary>View: empties the bar and ends the typing, with the bar still picked out.</summary>
    public void ClearTyping()
    {
        _view.SearchBox.Text = string.Empty;
        _view.SearchBar.Visibility = Visibility.Visible;
        _view.SearchBar.Focus();
    }

    /// <summary>Escape: ends the typing and keeps what was typed, with the bar picked out.</summary>
    public void EndTyping()
    {
        _view.SearchBar.Visibility = Visibility.Visible;
        _view.SearchBar.Focus();
    }

    // Which buttons end the search, in the middle of the box until there is something typed in it.
    private void UpdateHint() =>
        _view.SearchHint.Visibility = _navigation.Typing && _view.SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint();
        _searchTimer ??= NewSearchTimer();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private DispatcherTimer NewSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Apply();
        };
        return timer;
    }

    /// <summary>Narrows the games down to what is in the box, if that changed.</summary>
    private void Apply()
    {
        var text = _view.SearchBox.Text.Trim();
        if (text == Text || !_state.Filter.NotInstalled)
        {
            return;
        }

        Text = text;
        _showFound();
    }

    /// <summary>The store button: on to the next store's games, and after the last to every store's again.</summary>
    private void StepStore()
    {
        // The stores that have games which are not installed, in the order the button goes through them.
        var stores = _state.Shown(_options.ShowHidden).Where(t => !t.IsInstalled).Select(t => t.Game.Store).Distinct().Order().ToList();
        var index = Store is { } store ? stores.IndexOf(store) : -1;
        Store = index + 1 < stores.Count ? stores[index + 1] : null;
        Show(true);
        _showFound();
    }

    /// <summary>Menu or Enter: from the bar to the games found, or back to the bar when there are none.</summary>
    public void Finish()
    {
        _searchTimer?.Stop();
        Apply();
        if (_state.Tiles.Count == 0)
        {
            EndTyping();
        }
        else
        {
            _restoreFocus(true);
        }
    }

    /// <summary>Closes the search: the bar is emptied and put away with the store button, and every game shows again.</summary>
    public void Clear()
    {
        var inSearch = _navigation.Typing || _view.SearchBar.IsKeyboardFocused || _view.StoreButton.IsKeyboardFocused;
        Reset();
        _showAll();
        if (inSearch)
        {
            // The bar or the button the focus was on is gone.
            _view.SearchButton.Focus();
        }
        else if (!_isTopBarButton(Keyboard.FocusedElement))
        {
            _restoreFocus(true);
        }
    }

    /// <summary>Forgets the search without looking at the games again, as when its tab is left.</summary>
    public void Reset()
    {
        _searchTimer?.Stop();
        Text = string.Empty;
        Store = null;
        if (_view.SearchBox.Text.Length > 0)
        {
            _view.SearchBox.Text = string.Empty;
            _searchTimer?.Stop();
        }

        _view.SearchEntry.Visibility = Visibility.Collapsed;
        _view.SearchBar.Visibility = Visibility.Visible;
        Show(false);
    }
}

/// <summary>Which games a search finds.</summary>
internal static class GameSearch
{
    /// <summary>
    /// Whether the title has every word of the search in it, in any order and whatever the case. Letters
    /// and digits are all that count, so "witcher 3" finds "The Witcher® 3: Wild Hunt".
    /// </summary>
    public static bool Matches(string title, string search)
    {
        var plain = Plain(title);
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Plain)
            .All(word => plain.Contains(word, StringComparison.Ordinal));
    }

    private static string Plain(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));
}
