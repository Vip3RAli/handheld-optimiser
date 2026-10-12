using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Search on the Not installed tab, which can list hundreds of games: the top bar's search button opens
/// a box, and the games are narrowed down as each letter is typed. Beside the box, a button goes through
/// the stores to show one store's games. Menu or Enter goes to the games found, and View, Escape or B
/// among the games closes the search and shows them all again.
/// </summary>
public partial class LibraryWindow
{
    // What the games are narrowed down to now. Empty shows them all.
    private string _search = string.Empty;

    // The one store whose games are shown, or null for every store's.
    private GameStore? _searchStore;

    // A fast typist's letters are looked up together, so the grid is not rebuilt for each of them.
    private DispatcherTimer? _searchTimer;

    /// <summary>Whether the search box has the keyboard, when every key and button but a few is for typing.</summary>
    private bool IsSearching => SearchBox.IsKeyboardFocused;

    /// <summary>Whether the box and the store button are showing, until the search is closed.</summary>
    private bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;

    // Whether Windows' keyboard has been seen on screen since the typing began, and the timer that looks.
    private bool _keyboardSeen;
    private DispatcherTimer? _keyboardTimer;

    // Set while a press of A is acted on, which arrives as a click like a tap or the mouse does.
    private bool _gamepadPress;

    /// <summary>
    /// The search button. From the controller or the keyboard it opens the search with the bar picked
    /// out, to type in with A or to leave for the store button. A tap or a click goes straight to typing.
    /// </summary>
    private void OnOpenSearch(object sender, RoutedEventArgs e)
    {
        ShowSearch(true);
        if (_gamepadPress || InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SearchBar.Focus());
        }
        else
        {
            BeginTyping();
        }
    }

    private void OnBeginTyping(object sender, RoutedEventArgs e) => BeginTyping();

    /// <summary>Puts the cursor in the bar, which brings Windows' keyboard up.</summary>
    private void BeginTyping()
    {
        ShowSearch(true);
        SearchBar.Visibility = Visibility.Hidden;
        SearchEntry.Visibility = Visibility.Visible;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SearchBox.Focus());
    }

    private void ShowSearch(bool show)
    {
        SearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StoreButton.Visibility = SearchPanel.Visibility;
        StoreButton.Content = _searchStore is { } store ? Game.NameOf(store) : "All stores";
        ShowSearchText();
    }

    // What was searched for on the bar while it is not typed in, or what the bar is for.
    private void ShowSearchText()
    {
        var text = SearchBox.Text.Trim();
        SearchText.Text = text.Length > 0 ? text : "Search";
        SearchText.Opacity = text.Length > 0 ? 1 : 0.6;
    }

    private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSearchHint();

        // Windows does not bring its keyboard up for a box the controller focused, or with one plugged in.
        var window = new WindowInteropHelper(this).Handle;
        if (IsSearching)
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
        SearchEntry.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Visible;
        ShowSearchText();
        _searchTimer?.Stop();
        ApplySearch();
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
            if (!IsSearching)
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
    private void ClearTyping()
    {
        SearchBox.Text = string.Empty;
        SearchBar.Visibility = Visibility.Visible;
        SearchBar.Focus();
    }

    /// <summary>Escape: ends the typing and keeps what was typed, with the bar picked out.</summary>
    private void EndTyping()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchBar.Focus();
    }

    // Which buttons end the search, in the middle of the box until there is something typed in it.
    private void UpdateSearchHint() =>
        SearchHint.Visibility = IsSearching && SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateSearchHint();
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
            ApplySearch();
        };
        return timer;
    }

    /// <summary>Narrows the games down to what is in the box, if that changed.</summary>
    private void ApplySearch()
    {
        var text = SearchBox.Text.Trim();
        if (text == _search || !_filter.NotInstalled)
        {
            return;
        }

        _search = text;
        ShowFound();
    }

    /// <summary>The stores that have games which are not installed, in the order the button goes through them.</summary>
    private List<GameStore> SearchStores() =>
        ShownTiles().Where(t => !t.IsInstalled).Select(t => t.Game.Store).Distinct().Order().ToList();

    /// <summary>The store button: on to the next store's games, and after the last to every store's again.</summary>
    private void OnStepSearchStore(object sender, RoutedEventArgs e)
    {
        var stores = SearchStores();
        var index = _searchStore is { } store ? stores.IndexOf(store) : -1;
        _searchStore = index + 1 < stores.Count ? stores[index + 1] : null;
        ShowSearch(true);
        ShowFound();
    }

    /// <summary>Whether the search, with its store, leaves the game in.</summary>
    private bool Found(GameTile tile) =>
        (_searchStore is not { } store || tile.Game.Store == store)
        && (_search.Length == 0 || GameSearch.Matches(tile.Title, _search));

    /// <summary>Shows the games the search now finds, from the top.</summary>
    private void ShowFound()
    {
        ApplyFilter();

        // The pictures are loaded around the focus, which is in the top bar: the first games found get theirs.
        Scroller.ScrollToTop();
        if (_tiles.Count > 0)
        {
            _focusedKey = _tiles[0].Game.Key;
            LoadNotInstalledNear(0);
        }
    }

    /// <summary>Menu or Enter: from the bar to the games found, or back to the bar when there are none.</summary>
    private void FinishSearch()
    {
        _searchTimer?.Stop();
        ApplySearch();
        if (_tiles.Count == 0)
        {
            EndTyping();
        }
        else
        {
            RestoreFocus(leavingTopBar: true);
        }
    }

    /// <summary>Closes the search: the bar is emptied and put away with the store button, and every game shows again.</summary>
    private void ClearSearch()
    {
        var inSearch = IsSearching || SearchBar.IsKeyboardFocused || StoreButton.IsKeyboardFocused;
        ResetSearch();
        ApplyFilter();
        if (inSearch)
        {
            // The bar or the button the focus was on is gone.
            SearchButton.Focus();
        }
        else if (!IsTopBarButton(Keyboard.FocusedElement))
        {
            RestoreFocus(leavingTopBar: true);
        }
    }

    /// <summary>Forgets the search without looking at the games again, as when its tab is left.</summary>
    private void ResetSearch()
    {
        _searchTimer?.Stop();
        _search = string.Empty;
        _searchStore = null;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;
            _searchTimer?.Stop();
        }

        SearchEntry.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Visible;
        ShowSearch(false);
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
