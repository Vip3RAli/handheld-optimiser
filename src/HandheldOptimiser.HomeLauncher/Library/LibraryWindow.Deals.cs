using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Deals row below the grid: games on sale from IsThereAnyDeal, wishlist games first. Off until
/// switched on, and only in the grid layout on the Games tab. The deals are asked for when the library comes
/// to the front with no game running, at most every few hours (see GameDeals), and their banners are only
/// kept while the library is in front.
/// </summary>
public partial class LibraryWindow
{
    // Cards are 300 DIPs wide; 600 px covers up to 200% scaling.
    private const int DealBannerWidth = 600;

    private List<DealTile>? _deals;
    private bool _refreshingDeals;

    // The deals kept on disk are read off the UI thread; this goes up whenever the deals are replaced or
    // forgotten, so a read that was already on its way does not put back a list that is out of date.
    private bool _loadingDeals;
    private int _dealsVersion;
    private string? _dealsError;

    // Whether the focus is in the Deals row, and on which deal or card, to put it back there.
    private bool _focusInDeals;
    private string? _focusedDealId;

    // The cards for where the deals are from, the deals they were made for, and the one that is open.
    private List<DealSourceTile>? _dealSources;
    private List<DealTile>? _dealSourcesOf;
    private string? _openDealSource;

    /// <summary>Whether the row is on and has somewhere to get deals from: Epic's free games, or a key for the sales.</summary>
    private static bool DealsWanted =>
        LibrarySettings.ShowDeals && (LibrarySettings.EpicFreeGames || LibrarySettings.DealsKey is not null);

    /// <summary>The deals kept on disk: Epic's free games first, then the sales. Reads files: off the UI thread.</summary>
    private static List<Deal> AllDeals()
    {
        var deals = new List<Deal>();
        if (LibrarySettings.EpicFreeGames)
        {
            // Epic's links are made here rather than read, but are held to the same https-only rule.
            deals.AddRange(GameDeals.Safe(EpicFreeGames.Cached()));
        }

        if (LibrarySettings.DealsKey is not null)
        {
            deals.AddRange(GameDeals.Cached().Deals);
        }

        return deals;
    }

    /// <summary>Forgets the deals on show, so the next look reads them from disk again.</summary>
    private void ForgetDeals()
    {
        _deals = null;
        _dealsVersion++;
    }

    /// <summary>Reads the deals kept on disk on the thread pool, then fills the row with them.</summary>
    private async Task LoadDealsAsync()
    {
        if (_loadingDeals)
        {
            return;
        }

        _loadingDeals = true;
        var version = _dealsVersion;
        List<Deal> deals;
        try
        {
            // The files are a few hundred KB of JSON, which held up the first frame back from a game.
            deals = await Task.Run(AllDeals);
        }
        catch (Exception ex)
        {
            Program.Log($"Could not read the saved deals: {ex.Message}");
            return;
        }
        finally
        {
            _loadingDeals = false;
        }

        // Replaced or forgotten meanwhile: a newer list is already there, or will be read again below.
        if (version == _dealsVersion && _deals is null)
        {
            _deals = deals.Select(d => new DealTile(d)).ToList();
        }

        if (DealsWanted)
        {
            UpdateDeals();
        }
    }

    /// <summary>Fills the Deals row, or hides it when it is off or does not apply.</summary>
    private void UpdateDeals()
    {
        if (!DealsWanted)
        {
            DealsPanel.Visibility = Visibility.Collapsed;
            DealTiles.ItemsSource = null;
            ForgetDeals();
            _focusInDeals = false;
            RefreshCategories();
            return;
        }

        // Read from disk off the UI thread; the row fills in when they are in, a moment later.
        if (_deals is null)
        {
            _ = LoadDealsAsync();
            DealsPanel.Visibility = Visibility.Collapsed;
            _focusInDeals = false;
            RefreshCategories();
            return;
        }

        // The Old School layout has a category for the deals, there for as long as there are some.
        RefreshCategories();

        // Below the games on the Games tab, or in the Old School layout as the cards of their own category.
        var show = _deals.Count > 0 && (_layout == LibraryLayout.OldSchool ? OpenKind == CategoryKind.Deals : _filter == TileFilter.All);
        DealsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // Who the row's deals are from, beside its heading.
        var free = _deals.Any(d => d.Deal.Note is not null);
        var sales = _deals.Any(d => d.Deal.Note is null);
        DealsSource.Text = free && sales ? "Epic free games, prices from IsThereAnyDeal"
            : free ? "Free games from the Epic Games Store"
            : "Prices from IsThereAnyDeal";
        if (!show)
        {
            _focusInDeals = false;
            return;
        }

        // Below the grid every deal is in one row. Below the row of games there is a card for each place
        // they are from, and the deals of the one that was pressed in a grid under those. In the Old
        // School layout every deal is a card in its category's stack, which has no need of the heading.
        var row = IsRow;
        var cards = IsOldSchool;
        DealsHeading.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        DealsPanel.Margin = cards ? default : new Thickness(0, 18, 0, 0);
        List<DealTile>? shown = _deals;
        if (row)
        {
            if (!ReferenceEquals(_dealSourcesOf, _deals))
            {
                _dealSourcesOf = _deals;
                _dealSources = DealSourceTile.From(_deals);
            }

            var open = _dealSources?.Find(s => s.Key == _openDealSource);
            foreach (var source in _dealSources ?? [])
            {
                source.IsOpen = ReferenceEquals(source, open);
            }

            if (!ReferenceEquals(DealSources.ItemsSource, _dealSources))
            {
                DealSources.ItemsSource = _dealSources;
            }

            shown = open?.Deals;
        }

        DealSources.Visibility = row ? Visibility.Visible : Visibility.Collapsed;
        DealsScroller.Visibility = shown is null ? Visibility.Collapsed : Visibility.Visible;
        DealsScroller.HorizontalScrollBarVisibility = row || cards ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        DealsScroller.PanningMode = row || cards ? PanningMode.None : PanningMode.HorizontalOnly;

        var panel = (ItemsPanelTemplate)FindResource(cards ? "OldSchoolPanel" : row ? "GridPanel" : "RowPanel");
        if (!ReferenceEquals(DealTiles.ItemsPanel, panel))
        {
            DealTiles.ItemsPanel = panel;
            DealTiles.ItemTemplate = (DataTemplate)FindResource(cards ? "OldSchoolDeal" : "DealCard");
            DealTiles.Margin = cards ? new Thickness(10, 6, 10, 0) : default;
            DealCards.Fresh = cards;
        }

        if (!ReferenceEquals(DealTiles.ItemsSource, shown))
        {
            DealTiles.ItemsSource = shown;
        }

        QueueCardReset();
        LoadDealBanners();
    }

    // How far the wheel has turned over the Old School deals since it last moved on one, as for the games.
    private int _dealWheel;

    /// <summary>
    /// The mouse wheel over the deals: the row below the grid moves across with it, the Old School cards
    /// move on by one, and the grid of deals below the row of games passes it on to the page, which a
    /// scroll viewer does not do by itself.
    /// </summary>
    private void OnDealsWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (IsOldSchool)
        {
            // Each notch moves on to the next deal, as it does along the games' cards.
            _dealWheel += e.Delta;
            var steps = _dealWheel / Mouse.MouseWheelDeltaForOneLine;
            var buttons = DealButtons();
            if (steps != 0 && _menu is null && buttons.Count > 0)
            {
                _dealWheel -= steps * Mouse.MouseWheelDeltaForOneLine;
                buttons[Math.Clamp(Math.Max(0, DealCards.Front) - steps, 0, buttons.Count - 1)].Focus();
            }
        }
        else if (IsRow)
        {
            Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        }
        else
        {
            DealsScroller.ScrollToHorizontalOffset(DealsScroller.HorizontalOffset - e.Delta);
        }
    }

    // ----- The cards for where the deals are from, below the row of games -----

    private void OnDealSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DealSourceTile source })
        {
            return;
        }

        // Pressing the one that is open puts its deals away again.
        _openDealSource = source.Key == _openDealSource ? null : source.Key;
        UpdateDeals();
        AcceptHint.Text = source.IsOpen ? "Close" : "Open";
        if (source.IsOpen)
        {
            // Into view once they are laid out, with the cards still above them.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => DealsPanel.BringIntoView());
        }
    }

    private void OnDealSourceFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button { DataContext: DealSourceTile source } button)
        {
            return;
        }

        _focusInDeals = true;
        _focusedDealId = source.Key;
        AcceptHint.Text = source.IsOpen ? "Close" : "Open";
        button.BringIntoView(new Rect(-20, -60, button.ActualWidth + 40, button.ActualHeight + 100));
    }

    /// <summary>The cards for where the deals are from, left to right, while they are showing.</summary>
    private List<Button> DealSourceButtons() =>
        DealsPanel.Visibility == Visibility.Visible && DealSources.Visibility == Visibility.Visible ? Buttons(DealSources) : [];

    /// <summary>Left and right along the cards, up to the games, and down into the deals of the one that is open.</summary>
    private void NavigateDealSources(Button from, FocusNavigationDirection direction)
    {
        var buttons = DealSourceButtons();
        var index = buttons.IndexOf(from);
        switch (direction)
        {
            case FocusNavigationDirection.Left when index > 0:
                buttons[index - 1].Focus();
                break;
            case FocusNavigationDirection.Right when index >= 0 && index + 1 < buttons.Count:
                buttons[index + 1].Focus();
                break;
            case FocusNavigationDirection.Up when _tiles.Count > 0:
                // Back on the game the row was left from.
                FocusTile(Math.Max(0, _tiles.FindIndex(t => t.Game.Key == _focusedKey)));
                break;
            case FocusNavigationDirection.Down:
                DealButtons().FirstOrDefault()?.Focus();
                break;
        }
    }

    /// <summary>The buttons an items control has made for its items, in order.</summary>
    private static List<Button> Buttons(ItemsControl items)
    {
        var buttons = new List<Button>();
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (ItemContainers.At(items, i) is { } container
                && VisualChild<Button>(container) is { } button)
            {
                buttons.Add(button);
            }
        }

        return buttons;
    }

    /// <summary>
    /// Asks IsThereAnyDeal for the deals when the row is on and the last answer is old, or always when
    /// <paramref name="force"/> is set. Never while a game is running, unless the player asked.
    /// </summary>
    private async Task RefreshDealsAsync(bool force)
    {
        if (_refreshingDeals || !DealsWanted || (!force && _session is not null))
        {
            return;
        }

        _refreshingDeals = true;
        try
        {
            // Epic's free games need no key, and the sales do nothing without one. Both read and write their
            // files and parse what comes back, all on the thread pool; only the row is filled in here.
            var freeChanged = LibrarySettings.EpicFreeGames && await Task.Run(() => EpicFreeGames.RefreshAsync(force));
            var (changed, error) = await Task.Run(() => GameDeals.RefreshAsync(force));
            _dealsError = error;
            if (error is not null && ReferenceEquals(_menu, DealsItems))
            {
                Notice = error;
            }

            if (changed || freeChanged || _deals is null || _deals.Count == 0)
            {
                var deals = await Task.Run(AllDeals);
                _ = Task.Run(() => GameDeals.TrimBanners(deals));
                _deals = deals.Select(d => new DealTile(d)).ToList();
                _dealsVersion++;
                UpdateDeals();
            }
        }
        finally
        {
            _refreshingDeals = false;
        }

        if (ReferenceEquals(_menu, DealsItems))
        {
            RefreshDealsSettings();
        }
    }

    /// <summary>Downloads (once) and decodes the banners of the deals on show.</summary>
    private async void LoadDealBanners()
    {
        if (!IsActive || DealsPanel.Visibility != Visibility.Visible || _deals is not { } deals)
        {
            return;
        }

        foreach (var tile in deals.Where(t => t.Banner is null && !t.Loading && t.Deal.Image is not null).ToList())
        {
            tile.Loading = true;
            try
            {
                var banner = await Task.Run(async () =>
                    await GameDeals.BannerAsync(tile.Deal.Image!) is { } path ? GameTile.LoadCover(path, DealBannerWidth) : null);

                // The library may have gone to the background, or the deals been replaced, meanwhile.
                if (IsActive && ReferenceEquals(_deals, deals))
                {
                    tile.Banner = banner;
                }
            }
            catch (Exception ex)
            {
                // async void, so nothing else would catch it: that one card goes without its banner.
                Program.Log($"Could not load a deal's banner: {ex.Message}");
            }
            finally
            {
                tile.Loading = false;
            }
        }
    }

    /// <summary>Lets go of the banners while a game is in front.</summary>
    private void UnloadDeals()
    {
        foreach (var tile in _deals ?? [])
        {
            tile.Banner = null;
        }
    }

    /// <summary>The deal cards, in order, while they are showing.</summary>
    private List<Button> DealButtons() =>
        DealsPanel.Visibility == Visibility.Visible && DealsScroller.Visibility == Visibility.Visible ? Buttons(DealTiles) : [];

    /// <summary>
    /// Left and right along the deals. Below the grid they are one row, with the grid's last row above
    /// it. Below the row of games they are a grid of their own, with the cards they came from above it.
    /// </summary>
    private void NavigateDeals(Button from, FocusNavigationDirection direction)
    {
        var buttons = DealButtons();
        var index = buttons.IndexOf(from);
        if (IsOldSchool)
        {
            // One stack of every deal. Up and down are between the categories, before this is reached.
            NavigateCards(DealCards, direction);
            return;
        }

        if (IsRow && index >= 0)
        {
            // As many across as sit level with the first. The focused card is drawn a little bigger, which
            // moves its corner, so level is anywhere within half a card.
            var top = buttons[0].TranslatePoint(default, DealTiles).Y;
            var columns = Math.Max(1, buttons.TakeWhile(b => Math.Abs(b.TranslatePoint(default, DealTiles).Y - top) < b.ActualHeight / 2).Count());
            var last = buttons.Count - 1;
            switch (direction)
            {
                case FocusNavigationDirection.Left when index > 0:
                    buttons[index - 1].Focus();
                    break;
                case FocusNavigationDirection.Right when index < last:
                    buttons[index + 1].Focus();
                    break;
                case FocusNavigationDirection.Up when index >= columns:
                    buttons[index - columns].Focus();
                    break;
                case FocusNavigationDirection.Up:
                    var sources = DealSourceButtons();
                    (sources.Find(b => b.DataContext is DealSourceTile { IsOpen: true }) ?? sources.FirstOrDefault())?.Focus();
                    break;
                case FocusNavigationDirection.Down when index / columns < last / columns:
                    buttons[Math.Min(index + columns, last)].Focus();
                    break;
            }

            return;
        }

        switch (direction)
        {
            case FocusNavigationDirection.Left when index > 0:
                buttons[index - 1].Focus();
                break;
            case FocusNavigationDirection.Right when index >= 0 && index + 1 < buttons.Count:
                buttons[index + 1].Focus();
                break;
            case FocusNavigationDirection.Up when _tiles.Count > 0:
                var columns = ColumnCount();
                var lastRow = Enumerable.Range((_tiles.Count - 1) / columns * columns, _tiles.Count - (_tiles.Count - 1) / columns * columns)
                    .Select(i => TileContainer(i) is { } c ? VisualChild<Button>(c) : null)
                    .OfType<Button>()
                    .ToList();
                (Nearest(lastRow, from) ?? lastRow.LastOrDefault())?.Focus();
                break;
        }
    }

    /// <summary>Down from the grid's last row: the deal below it.</summary>
    private bool FocusDealBelow(Button from)
    {
        // Below the row of games that is a card for where the deals are from.
        if (Nearest(IsRow ? DealSourceButtons() : DealButtons(), from) is not { } below)
        {
            return false;
        }

        below.Focus();
        return true;
    }

    /// <summary>Back on the deal the focus was on, when the library returns to the front.</summary>
    private bool RestoreDealFocus()
    {
        if (!_focusInDeals
            || (DealButtons().Find(b => b.DataContext is DealTile d && d.Deal.Id == _focusedDealId)
                ?? DealSourceButtons().Find(b => b.DataContext is DealSourceTile s && s.Key == _focusedDealId)) is not { } button)
        {
            return false;
        }

        button.Focus();
        return true;
    }

    private void OnDealFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button { DataContext: DealTile tile } button)
        {
            return;
        }

        _focusInDeals = true;
        _focusedDealId = tile.Deal.Id;
        AcceptHint.Text = "View deal";

        // The Old School cards all sit in one place and come to the focus, so only the page moves.
        if (IsOldSchool)
        {
            CardFocused(DealCards, tile, _deals?.IndexOf(tile) ?? -1);
            ShowCategoryBackdrop();
            return;
        }

        button.BringIntoView(new Rect(-20, -40, button.ActualWidth + 40, button.ActualHeight + 80));
    }

    private void OnDealClick(object sender, RoutedEventArgs e)
    {
        // A tap on an Old School card behind the front one only brings it forward.
        if (sender is Button { DataContext: DealTile tile } && !CardClickSwallowed(DealCards, tile))
        {
            OpenDeal(tile);
        }
    }

    /// <summary>Opens the deal's store page in the browser: through IsThereAnyDeal's own link for a sale.</summary>
    private void OpenDeal(DealTile tile)
    {
        // Checked again right before the shell gets it, whatever path the deal came by.
        if (!GameDeals.IsHttpsUrl(tile.Deal.Url))
        {
            Program.Log($"Did not open the deal for {tile.Deal.Title}: its link is not an https address");
            Notice = "That deal's link could not be opened.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(tile.Deal.Url) { UseShellExecute = true })?.Dispose();
            Program.Log($"Opened the deal for {tile.Deal.Title} at {tile.Deal.Shop}");
            Notice = $"Opening {tile.Deal.Title} at {tile.Deal.Shop}...";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Program.Log($"Could not open the deal for {tile.Deal.Title}: {ex.Message}");
            Notice = "The browser could not be opened.";
        }
    }

    /// <summary>What A does on the tab that is open.</summary>
    private string AcceptText() => _filter.NotInstalled ? "Install" : _filter.Apps ? "Open" : "Play";

    // ----- Settings: Game deals -----

    private void OnOpenDealsSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsGroup(DealsItems, "Game deals", GamesApps);
        RefreshDealsSettings();
        Notice = _dealsError ?? string.Empty;
    }

    private void OnToggleDeals(object sender, RoutedEventArgs e)
    {
        LibrarySettings.ShowDeals = !LibrarySettings.ShowDeals;
        RefreshDealsSettings();
        UpdateDeals();
        _ = RefreshDealsAsync(force: false);
    }

    private void OnEditDealsKey(object sender, RoutedEventArgs e) =>
        BeginEdit(MenuEditor.DealsKey, "IsThereAnyDeal API key",
            "Free at isthereanydeal.com/apps/my: register an app and copy its API key. Only sent to IsThereAnyDeal, with your wishlist if that is on. Leave empty to remove it.",
            LibrarySettings.DealsKey);

    private void OnToggleEpicFreeGames(object sender, RoutedEventArgs e)
    {
        LibrarySettings.EpicFreeGames = !LibrarySettings.EpicFreeGames;
        ForgetDeals();
        RefreshDealsSettings();
        UpdateDeals();
        _ = RefreshDealsAsync(force: false);
    }

    private void OnToggleWishlistDeals(object sender, RoutedEventArgs e)
    {
        LibrarySettings.WishlistDeals = !LibrarySettings.WishlistDeals;
        RefreshDealsSettings();

        // What it needs to work was under the row's name, and is said once as it is switched on.
        if (LibrarySettings.WishlistDeals)
        {
            Notice = LibrarySettings.SteamKey is null ? "Wishlist deals need a Steam Web API key. Add it under Games and apps, Games not installed."
                : "Your Steam wishlist must be public for this to work.";
        }
        _ = RefreshDealsAsync(force: true);
    }

    private void OnRefreshDeals(object sender, RoutedEventArgs e)
    {
        DealsRefreshItem.Tag = "Checking...";
        _ = RefreshDealsAsync(force: true);
    }

    /// <summary>Saves a new key from the text box, then asks for the deals with it.</summary>
    private void SaveDealsKey(string text)
    {
        LibrarySettings.DealsKey = text;
        _dealsError = null;
        ForgetDeals();
        RefreshDealsSettings();
        ShowMenuItems();
        UpdateDeals();
        if (text.Length > 0)
        {
            DealsKeySetting.Tag = "Checking...";
            _ = RefreshDealsAsync(force: true);
        }
    }

    private void RefreshDealsSettings()
    {
        var on = LibrarySettings.ShowDeals;
        var key = LibrarySettings.DealsKey;
        var epic = LibrarySettings.EpicFreeGames;
        DealsSetting.Tag = OnOff(on);

        EpicFreeSetting.IsEnabled = on;
        EpicFreeSetting.Tag = OnOff(epic);

        // What went wrong with a key is said in a notice, and where to get one on the screen it is typed in.
        DealsKeySetting.IsEnabled = on;
        DealsKeySetting.Tag = key is null ? "Not set" : _dealsError is null ? "Set" : "Not working";

        var wishlist = LibrarySettings.WishlistDeals;
        WishlistDealsSetting.IsEnabled = on;
        WishlistDealsSetting.Tag = OnOff(wishlist);

        DealsRefreshItem.IsEnabled = on && (epic || key is not null);
        DealsRefreshItem.Tag = null;
    }
}

/// <summary>
/// A card for one place the deals are from, below the row of games: Epic's free games, or the sales.
/// Pressing it shows its deals in a grid underneath.
/// </summary>
internal sealed class DealSourceTile(string key, string title, string detail, List<DealTile> deals) : INotifyPropertyChanged, INoticeHost
{
    private bool _isOpen;
    private string? _notice;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public List<DealTile> Deals { get; } = deals;

    /// <summary>The first deal's banner, which arrives after the card is shown.</summary>
    public ImageSource? Banner => Deals.Count > 0 ? Deals[0].Banner : null;

    /// <summary>Whether its deals are the ones showing underneath.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen != value)
            {
                _isOpen = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
            }
        }
    }

    /// <summary>What the library has to say about these deals, shown on their card in place of its name.</summary>
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

    /// <summary>A card for each kind of deal there is: the giveaways, then the sales.</summary>
    public static List<DealSourceTile> From(List<DealTile> deals)
    {
        var sources = new List<DealSourceTile>();
        var free = deals.Where(d => d.Deal.Note is not null).ToList();
        if (free.Count > 0)
        {
            var now = free.Count(d => d.Deal.Cut == 100);
            var soon = free.Count - now;
            sources.Add(new DealSourceTile("source:free", "Free on Epic",
                string.Join(", ", new[] { now > 0 ? $"{now} free this week" : null, soon > 0 ? $"{soon} coming next" : null }.OfType<string>()),
                free));
        }

        var sales = deals.Where(d => d.Deal.Note is null).ToList();
        if (sales.Count > 0)
        {
            var best = sales.Max(d => d.Deal.Cut);
            sources.Add(new DealSourceTile("source:sales", "Game deals",
                $"{sales.Count} on sale, up to {best}% off", sales));
        }

        foreach (var source in sources)
        {
            // The card's picture is its first deal's, which is loaded after the card is made.
            source.Deals[0].PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DealTile.Banner))
                {
                    source.PropertyChanged?.Invoke(source, new PropertyChangedEventArgs(nameof(Banner)));
                }
            };
        }

        return sources;
    }
}

/// <summary>One deal card in the Deals row.</summary>
internal sealed class DealTile(Deal deal) : INotifyPropertyChanged, INoticeHost
{
    private ImageSource? _banner;
    private string? _notice;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Deal Deal { get; } = deal;

    public string Title => Deal.Title;
    public string Price => Deal.Price;

    /// <summary>The shop, and the day the deal ends when it has one.</summary>
    public string Shop => Deal.Expiry is { } ends ? $"{Deal.Shop}, ends {ends.ToLocalTime():ddd d MMM}" : Deal.Shop;

    /// <summary>The regular price, only when it differs from the sale price.</summary>
    public string? Regular => Deal.Regular == Deal.Price ? null : Deal.Regular;

    /// <summary>How much is off, or for a giveaway whether it is free yet.</summary>
    public string Cut => Deal.Note is not null ? (Deal.Cut == 100 ? "Free" : "Soon")
        : Deal.Cut > 0 ? $"-{Deal.Cut}%"
        : "";

    /// <summary>A giveaway's note, Wishlist or Lowest ever, or nothing.</summary>
    public string? Badge => Deal.Note ?? (Deal.OnWishlist ? "On your wishlist" : Deal.HistoricalLow ? "Lowest ever" : null);

    public bool HasBadge => Badge is not null;

    /// <summary>What the library has to say about the deal, shown on its card in place of its name.</summary>
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

    public bool Loading { get; set; }

    public ImageSource? Banner
    {
        get => _banner;
        set
        {
            if (!ReferenceEquals(_banner, value))
            {
                _banner = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Banner)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsTitle)));
            }
        }
    }

    /// <summary>The title across the card, while it has no banner.</summary>
    public bool ShowsTitle => _banner is null;
}
