using System.ComponentModel;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

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
