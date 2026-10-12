using System.Windows;
using System.Windows.Input;
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
public partial class LibraryWindow
{
    private static readonly TimeSpan NoticeShownFor = TimeSpan.FromSeconds(5);

    // A game takes a while to come to the front, and the library going behind it clears the notice.
    private static readonly TimeSpan LaunchNoticeShownFor = TimeSpan.FromSeconds(15);

    // The notice that is showing, the tile or card it was asked to be shown on, if one was named, and
    // the one it is on now.
    private string? _notice;
    private INoticeHost? _noticeFor;
    private INoticeHost? _noticeOn;
    private DispatcherTimer? _noticeTimer;

    /// <summary>Shows a notice for a few seconds, wherever the player is looking. Empty takes the last one away.</summary>
    private string Notice
    {
        set => ShowNotice(value);
    }

    /// <summary>Shows a notice, on the tile or card it is about when one is named, for longer when it needs it.</summary>
    private void ShowNotice(string? text, INoticeHost? on = null, TimeSpan? shownFor = null)
    {
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
        _noticeFor = on;
        PlaceNotice();

        _noticeTimer?.Stop();
        if (_notice is null)
        {
            return;
        }

        if (_noticeTimer is null)
        {
            _noticeTimer = new DispatcherTimer();
            _noticeTimer.Tick += (_, _) => ShowNotice(null);
        }

        _noticeTimer.Interval = shownFor ?? NoticeShownFor;
        _noticeTimer.Start();
    }

    /// <summary>
    /// Puts the notice where it can be read now: in the menu while one is open, and on its tile or card
    /// otherwise. Called again as the menu closes, so a notice raised from a menu row is not lost with it.
    /// </summary>
    private void PlaceNotice()
    {
        if (_noticeOn is { } was)
        {
            was.Notice = null;
            _noticeOn = null;
        }

        MenuNotice.Visibility = Visibility.Collapsed;
        if (_notice is null)
        {
            return;
        }

        if (_menu is not null)
        {
            MenuNotice.Text = _notice;
            MenuNotice.Visibility = Visibility.Visible;
        }
        else if ((_noticeFor ?? NoticeHost()) is { } host)
        {
            host.Notice = _notice;
            _noticeOn = host;
        }
    }

    /// <summary>
    /// The tile or card the player is looking at: the front card in the Old School layout, and in the
    /// others whatever has the focus, or the game it was last on.
    /// </summary>
    private INoticeHost? NoticeHost()
    {
        if (OpenStack is { } stack && stack.Front >= 0 && stack.Front < stack.Items.Items.Count
            && stack.Items.Items[stack.Front] is INoticeHost card)
        {
            return card;
        }

        return Keyboard.FocusedElement is FrameworkElement { DataContext: INoticeHost focused } ? focused
            : _tiles.Find(t => t.Game.Key == _focusedKey);
    }
}
