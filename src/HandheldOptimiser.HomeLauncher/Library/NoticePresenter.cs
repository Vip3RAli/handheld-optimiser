using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Something a notice used to be shown on in place of its name: a game's tile or card, a deal's, or a green card.</summary>
internal interface INoticeHost
{
    /// <summary>What the library has to say about it, or null when it has nothing to say.</summary>
    string? Notice { get; set; }
}

/// <summary>
/// What the library has to say: that a game is starting, that something was saved, what TDP Engine is doing,
/// or that something could not be done. Every notice is shown in one place of its own at the bottom centre
/// of the library, which pops in, stays a few seconds and fades out, as an achievement does on an Xbox. The
/// user asked for that on 2026-11-15; before, a notice took the place of the name on the tile or card it was
/// about, one about a game not on show had a panel, and one raised while a menu was open was a line at the
/// top of the menu. It is drawn over the menu, so it shows the same whether one is open or not.
/// </summary>
internal sealed class NoticePresenter
{
    private static readonly Duration PopIn = TimeSpan.FromMilliseconds(220);
    private static readonly Duration FadeOut = TimeSpan.FromMilliseconds(450);

    // How far above its place the notice starts as it pops in at top centre.
    private const double Rise = -20;

    /// <summary>The notice's own place at the bottom centre of the library, and the text in it.</summary>
    public (Border Panel, TextBlock Text)? Panel { get; init; }

    /// <summary>Whether the library is in front, where a notice can be read.</summary>
    public Func<bool>? InFront { get; init; }

    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(5);

    /// <summary>A game takes a while to come to the front, and the library going behind it clears the notice.</summary>
    public static readonly TimeSpan LaunchShownFor = TimeSpan.FromSeconds(15);

    // The notice that is showing, and whether its place is on show or on its way in.
    private string? _notice;
    private bool _up;
    private DispatcherTimer? _timer;

    // A notice that came while another window was in front, kept for when the library is back.
    private string? _whenBack;

    /// <summary>Shows a notice about a game. The tile is no longer where it goes; it is kept so callers say what a notice is about.</summary>
    public void ShowAbout(string? text, INoticeHost? tile, TimeSpan? shownFor = null) => Show(text, tile, shownFor);

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

        Show(text, null, TimeSpan.FromSeconds(12));
    }

    /// <summary>The library is in front again: a notice kept for it is shown.</summary>
    public void Back()
    {
        if (_whenBack is { } text)
        {
            _whenBack = null;
            Show(text, null, TimeSpan.FromSeconds(12));
        }
    }

    /// <summary>Shows a notice, for longer when it needs it. Empty takes the last one away.</summary>
    public void Show(string? text, INoticeHost? on = null, TimeSpan? shownFor = null)
    {
        _ = on;
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
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

    /// <summary>Shows the notice in its place, or takes it away. Still called as a menu opens and closes, which changes nothing now.</summary>
    public void Place()
    {
        if (_notice is null)
        {
            Lower();
            return;
        }

        Raise(_notice);
    }

    /// <summary>Pops the notice in, or changes what one already up says.</summary>
    private void Raise(string text)
    {
        if (Panel is not { } panel)
        {
            return;
        }

        panel.Text.Text = text;
        if (_up)
        {
            return;
        }

        _up = true;
        panel.Panel.Visibility = Visibility.Visible;
        var slide = Slide(panel.Panel);
        panel.Panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(panel.Panel.Opacity, 1, PopIn));
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Rise, 0, PopIn) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Fades the notice out.</summary>
    private void Lower()
    {
        if (Panel is not { } panel || !_up)
        {
            return;
        }

        _up = false;
        var fadeOut = new DoubleAnimation(panel.Panel.Opacity, 0, FadeOut);
        fadeOut.Completed += (_, _) =>
        {
            // Unless another notice came up meanwhile.
            if (!_up)
            {
                panel.Panel.Visibility = Visibility.Collapsed;
            }
        };
        panel.Panel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static TranslateTransform Slide(Border panel)
    {
        if (panel.RenderTransform is not TranslateTransform slide)
        {
            panel.RenderTransform = slide = new TranslateTransform();
        }

        return slide;
    }
}
