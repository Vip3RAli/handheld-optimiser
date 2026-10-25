using System.ComponentModel;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

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
