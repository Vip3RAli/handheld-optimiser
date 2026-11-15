using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Something a notice can be shown on in place of its name: a game's tile or card, a deal's, or a green card.</summary>
internal interface INoticeHost
{
    /// <summary>What the library has to say about it, or null when it has nothing to say.</summary>
    string? Notice { get; set; }
}

/// <summary>
/// What the library has to say: that a game is starting, that something was saved, or that it could not
/// be. There is no line of its own for this. A notice is shown on the tile or card it is about, where the
/// player is already looking, for a few seconds, and at the top of the menu while one is open.
/// </summary>
/// <param name="menuNotice">The line at the top of the menu.</param>
/// <param name="lookingAt">The tile or card the player is looking at, for a notice that names none.</param>
internal sealed class NoticePresenter(TextBlock menuNotice, LibraryNavigation navigation, Func<INoticeHost?> lookingAt)
{
    /// <summary>
    /// The panel above the button hints, for a notice about a game whose tile is not on show. Without it such a
    /// notice went on whatever tile had the focus: on 2026-11-14 "AdVenture Capitalist is paused" sat on
    /// GWENT's tile in Ready to install.
    /// </summary>
    public (Border Panel, TextBlock Text)? Panel { get; init; }

    // The notice belongs in the panel rather than on a tile.
    private bool _inPanel;

    /// <summary>
    /// Shows a notice about a game: on its tile when the tile is on show, and in the panel otherwise, never on
    /// another game's tile.
    /// </summary>
    public void ShowAbout(string? text, INoticeHost? tile, TimeSpan? shownFor = null)
    {
        if (tile is null && Panel is not null)
        {
            Show(text, null, shownFor);
            _inPanel = _notice is not null;
            Place();
            return;
        }

        Show(text, tile, shownFor);
    }

    /// <summary>Whether the library is in front, where a notice can be read.</summary>
    public Func<bool>? InFront { get; init; }

    // A notice that came while another window was in front, kept for when the library is back.
    private string? _whenBack;

    /// <summary>
    /// Shows a notice now when the library is in front, and as it comes back otherwise: the library going
    /// behind another window clears whatever notice is up, so news about a store app it has just opened
    /// would never be read.
    /// </summary>
    public void ShowWhenInFront(string text)
    {
        if (InFront?.Invoke() == false)
        {
            _whenBack = text;
            return;
        }

        ShowAbout(text, null, TimeSpan.FromSeconds(12));
    }

    /// <summary>The library is in front again: a notice kept for it is shown.</summary>
    public void Back()
    {
        if (_whenBack is { } text)
        {
            _whenBack = null;
            ShowAbout(text, null, TimeSpan.FromSeconds(12));
        }
    }

    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(5);

    /// <summary>A game takes a while to come to the front, and the library going behind it clears the notice.</summary>
    public static readonly TimeSpan LaunchShownFor = TimeSpan.FromSeconds(15);

    // The notice that is showing, the tile or card it was asked to be shown on, if one was named, and
    // the one it is on now.
    private string? _notice;
    private INoticeHost? _for;
    private INoticeHost? _on;
    private DispatcherTimer? _timer;

    /// <summary>Shows a notice, on the tile or card it is about when one is named, for longer when it needs it. Empty takes the last one away.</summary>
    public void Show(string? text, INoticeHost? on = null, TimeSpan? shownFor = null)
    {
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
        _for = on;
        _inPanel = false;
        Place();

        _timer?.Stop();
        if (_notice is null)
        {
            return;
        }

        if (_timer is null)
        {
            _timer = new DispatcherTimer();
            _timer.Tick += (_, _) => Show(null);
        }

        _timer.Interval = shownFor ?? ShownFor;
        _timer.Start();
    }

    /// <summary>
    /// Puts the notice where it can be read now: in the menu while one is open, and on its tile or card
    /// otherwise. Called again as the menu closes, so a notice raised from a menu row is not lost with it.
    /// </summary>
    public void Place()
    {
        if (_on is { } was)
        {
            was.Notice = null;
            _on = null;
        }

        menuNotice.Visibility = Visibility.Collapsed;
        if (Panel is { } hidden)
        {
            hidden.Panel.Visibility = Visibility.Collapsed;
        }

        if (_notice is null)
        {
            return;
        }

        if (navigation.MenuOpen)
        {
            menuNotice.Text = _notice;
            menuNotice.Visibility = Visibility.Visible;
        }
        else if (_inPanel && Panel is { } panel)
        {
            panel.Text.Text = _notice;
            panel.Panel.Visibility = Visibility.Visible;
        }
        else if ((_for ?? lookingAt()) is { } host)
        {
            host.Notice = _notice;
            _on = host;
        }
    }
}
