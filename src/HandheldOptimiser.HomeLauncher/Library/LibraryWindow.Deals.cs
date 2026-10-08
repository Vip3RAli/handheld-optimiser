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
    private string? _dealsError;

    // Whether the focus is in the Deals row, and on which deal, to put it back there.
    private bool _focusInDeals;
    private string? _focusedDealId;

    /// <summary>Fills the Deals row, or hides it when it is off or does not apply.</summary>
    private void UpdateDeals()
    {
        if (!LibrarySettings.ShowDeals || LibrarySettings.DealsKey is null)
        {
            DealsPanel.Visibility = Visibility.Collapsed;
            DealTiles.ItemsSource = null;
            _deals = null;
            _focusInDeals = false;
            return;
        }

        _deals ??= GameDeals.Cached().Deals.Select(d => new DealTile(d)).ToList();

        var show = _layout == LibraryLayout.Grid && _filter == TileFilter.All && _deals.Count > 0;
        DealsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            _focusInDeals = false;
            return;
        }

        if (!ReferenceEquals(DealTiles.ItemsSource, _deals))
        {
            DealTiles.ItemsSource = _deals;
        }

        LoadDealBanners();
    }

    /// <summary>
    /// Asks IsThereAnyDeal for the deals when the row is on and the last answer is old, or always when
    /// <paramref name="force"/> is set. Never while a game is running, unless the player asked.
    /// </summary>
    private async Task RefreshDealsAsync(bool force)
    {
        if (_refreshingDeals || !LibrarySettings.ShowDeals || LibrarySettings.DealsKey is null || (!force && _session is not null))
        {
            return;
        }

        _refreshingDeals = true;
        try
        {
            var (changed, error) = await GameDeals.RefreshAsync(force);
            _dealsError = error;
            if (changed || _deals is null || _deals.Count == 0)
            {
                var deals = GameDeals.Cached().Deals;
                _ = Task.Run(() => GameDeals.TrimBanners(deals));
                _deals = deals.Select(d => new DealTile(d)).ToList();
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

    /// <summary>The deal cards, left to right, while the row is showing.</summary>
    private List<Button> DealButtons()
    {
        if (DealsPanel.Visibility != Visibility.Visible)
        {
            return [];
        }

        var buttons = new List<Button>();
        for (var i = 0; i < DealTiles.Items.Count; i++)
        {
            if (DealTiles.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                && VisualChild<Button>(container) is { } button)
            {
                buttons.Add(button);
            }
        }

        return buttons;
    }

    /// <summary>Left and right along the Deals row, and up into the grid's last row.</summary>
    private void NavigateDeals(Button from, FocusNavigationDirection direction)
    {
        var buttons = DealButtons();
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
        if (Nearest(DealButtons(), from) is not { } below)
        {
            return false;
        }

        below.Focus();
        return true;
    }

    /// <summary>Back on the deal the focus was on, when the library returns to the front.</summary>
    private bool RestoreDealFocus()
    {
        if (!_focusInDeals || DealButtons().Find(b => b.DataContext is DealTile d && d.Deal.Id == _focusedDealId) is not { } button)
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
        StatusText.Text = tile.Status;
        button.BringIntoView(new Rect(-20, -40, button.ActualWidth + 40, button.ActualHeight + 80));
    }

    private void OnDealClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DealTile tile })
        {
            OpenDeal(tile);
        }
    }

    /// <summary>Opens the deal's store page in the browser, through IsThereAnyDeal's own link.</summary>
    private void OpenDeal(DealTile tile)
    {
        try
        {
            Process.Start(new ProcessStartInfo(tile.Deal.Url) { UseShellExecute = true })?.Dispose();
            Program.Log($"Opened the deal for {tile.Deal.Title} at {tile.Deal.Shop}");
            StatusText.Text = $"Opening {tile.Deal.Title} at {tile.Deal.Shop}...";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Program.Log($"Could not open the deal for {tile.Deal.Title}: {ex.Message}");
            StatusText.Text = "The browser could not be opened.";
        }
    }

    /// <summary>What A does on the tab that is open.</summary>
    private string AcceptText() => _filter.NotInstalled ? "Install" : _filter.Apps ? "Open" : "Play";

    // ----- Settings: Game deals -----

    private void OnOpenDealsSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsGroup(DealsItems, "Game deals");
        RefreshDealsSettings();
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
            "Free at isthereanydeal.com/apps/my once signed in: register an app and copy its API key. Only sent to IsThereAnyDeal, with your wishlist's games when that is on. Leave empty to remove the key.",
            LibrarySettings.DealsKey);

    private void OnToggleWishlistDeals(object sender, RoutedEventArgs e)
    {
        LibrarySettings.WishlistDeals = !LibrarySettings.WishlistDeals;
        RefreshDealsSettings();
        _ = RefreshDealsAsync(force: true);
    }

    private void OnRefreshDeals(object sender, RoutedEventArgs e)
    {
        DealsRefreshItem.Tag = "Looking for deals...";
        _ = RefreshDealsAsync(force: true);
    }

    /// <summary>Saves a new key from the text box, then asks for the deals with it.</summary>
    private void SaveDealsKey(string text)
    {
        LibrarySettings.DealsKey = text;
        _dealsError = null;
        _deals = null;
        RefreshDealsSettings();
        ShowMenuItems();
        UpdateDeals();
        if (text.Length > 0)
        {
            DealsKeySetting.Tag = "Asking IsThereAnyDeal for deals...";
            _ = RefreshDealsAsync(force: true);
        }
    }

    private void RefreshDealsSettings()
    {
        var on = LibrarySettings.ShowDeals;
        var key = LibrarySettings.DealsKey;
        var count = _deals?.Count ?? 0;
        DealsSetting.Tag = !on ? "Off"
            : key is null ? "On. Needs an IsThereAnyDeal API key, below"
            : _layout != LibraryLayout.Grid ? "On. Shows below the games in the grid layout"
            : count == 0 ? "On. No deals yet"
            : $"On, below the games on the Games tab. {count} deals";

        DealsKeySetting.IsEnabled = on;
        DealsKeySetting.Tag = key is null ? "Not set. Free at isthereanydeal.com/apps/my"
            : _dealsError is { } error ? error
            : $"Set, ending in {key[^Math.Min(4, key.Length)..]}";

        var wishlist = LibrarySettings.WishlistDeals;
        WishlistDealsSetting.IsEnabled = on;
        WishlistDealsSetting.Tag = !wishlist ? "Off"
            : LibrarySettings.SteamKey is null ? "On. Needs the Steam Web API key under Games not installed"
            : "On. Your Steam wishlist must be public";

        DealsRefreshItem.IsEnabled = on && key is not null;
        DealsRefreshItem.Tag = GameDeals.Cached().Fetched is { } fetched
            ? $"Ask IsThereAnyDeal now. Last asked {fetched.ToLocalTime():t} on {fetched.ToLocalTime():ddd d MMM}"
            : "Ask IsThereAnyDeal now";
    }
}

/// <summary>One deal card in the Deals row.</summary>
internal sealed class DealTile(Deal deal) : INotifyPropertyChanged
{
    private ImageSource? _banner;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Deal Deal { get; } = deal;

    public string Title => Deal.Title;
    public string Price => Deal.Price;
    public string Shop => Deal.Shop;

    /// <summary>The regular price, only when it differs from the sale price.</summary>
    public string? Regular => Deal.Regular == Deal.Price ? null : Deal.Regular;

    public string Cut => Deal.Cut > 0 ? $"-{Deal.Cut}%" : "";

    /// <summary>Wishlist or Lowest ever, or nothing.</summary>
    public string? Badge => Deal.OnWishlist ? "On your wishlist" : Deal.HistoricalLow ? "Lowest ever" : null;

    public bool HasBadge => Badge is not null;

    /// <summary>The line under the library while the card has the focus.</summary>
    public string Status =>
        $"{Deal.Title}: {Deal.Price} at {Deal.Shop}"
        + (Deal.HistoricalLow ? ", the lowest it has been" : "")
        + (Deal.Expiry is { } ends ? $". Ends {ends.ToLocalTime():ddd d MMM}" : "");

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
