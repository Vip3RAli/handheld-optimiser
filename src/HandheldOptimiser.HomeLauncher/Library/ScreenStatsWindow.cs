using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The stats screen's window: it fills the handheld's panel with the time and six big tiles. It is drawn at
/// 1280 by 720 and scaled to the panel, whatever its size and scaling. It never takes the keyboard or the
/// controller from the game, and is not in the taskbar or Alt+Tab. Built in code with NXE Evolution styling.
/// </summary>
internal sealed partial class ScreenStatsWindow : Window
{
    private const int ExStyle = -20;
    private const int ExToolWindow = 0x00000080;
    private const int ExNoActivate = 0x08000000;

    private static readonly nint TopMost = -1;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindowFlag = 0x0040;

    private static readonly FontFamily Blocky = new("Bahnschrift, Segoe UI");

    // Canvas background: NXE deep dimensional slate gradient
    private static readonly Brush Page = FrozenLinearGradient(
        new Point(0, 0), new Point(0, 1),
        (0.0, Color.FromRgb(0x0C, 0x10, 0x18)),
        (0.5, Color.FromRgb(0x08, 0x0A, 0x10)),
        (1.0, Color.FromRgb(0x05, 0x06, 0x09)));

    // Standard card surfaces and borders
    private static readonly Brush CardFill = FrozenLinearGradient(
        new Point(0, 0), new Point(0, 1),
        (0.0, Color.FromRgb(0x1B, 0x22, 0x30)),
        (0.5, Color.FromRgb(0x13, 0x18, 0x23)),
        (1.0, Color.FromRgb(0x0E, 0x12, 0x1A)));

    private static readonly Brush CardBorder = FrozenLinearGradient(
        new Point(0, 0), new Point(0, 1),
        (0.0, Color.FromRgb(0x35, 0x43, 0x5C)),
        (0.5, Color.FromRgb(0x23, 0x2D, 0x3E)),
        (1.0, Color.FromRgb(0x16, 0x1D, 0x29)));

    private static readonly Brush CardSheen = FrozenLinearGradient(
        new Point(0, 0), new Point(1, 0),
        (0.0, Color.FromArgb(0x70, 0x4F, 0x63, 0x82)),
        (0.7, Color.FromArgb(0x25, 0x35, 0x43, 0x5C)),
        (1.0, Color.FromArgb(0x00, 0x16, 0x1D, 0x29)));

    // Hero card (FPS) surfaces and borders: signature Xbox Emerald Green accents
    private static readonly Brush HeroCardFill = FrozenLinearGradient(
        new Point(0, 0), new Point(0, 1),
        (0.0, Color.FromRgb(0x15, 0x24, 0x1C)),
        (0.4, Color.FromRgb(0x11, 0x1A, 0x20)),
        (1.0, Color.FromRgb(0x0E, 0x13, 0x1A)));

    private static readonly Brush HeroCardBorder = FrozenLinearGradient(
        new Point(0, 0), new Point(0, 1),
        (0.0, Color.FromRgb(0x1E, 0xD7, 0x60)),
        (0.5, Color.FromRgb(0x10, 0x7C, 0x10)),
        (1.0, Color.FromRgb(0x1B, 0x35, 0x24)));

    private static readonly Brush HeroCardSheen = FrozenLinearGradient(
        new Point(0, 0), new Point(1, 0),
        (0.0, Color.FromRgb(0x1E, 0xD7, 0x60)),
        (0.7, Color.FromRgb(0x10, 0x7C, 0x10)),
        (1.0, Color.FromArgb(0x00, 0x10, 0x7C, 0x10)));

    // Header divider: glowing Xbox emerald glow bar
    private static readonly Brush HeaderDivider = FrozenLinearGradient(
        new Point(0, 0), new Point(1, 0),
        (0.0, Color.FromArgb(0x00, 0x10, 0x7C, 0x10)),
        (0.15, Color.FromRgb(0x10, 0x7C, 0x10)),
        (0.5, Color.FromRgb(0x1E, 0xD7, 0x60)),
        (0.85, Color.FromRgb(0x10, 0x7C, 0x10)),
        (1.0, Color.FromArgb(0x00, 0x10, 0x7C, 0x10)));

    // Pill badge brushes
    private static readonly Brush BadgeBackground = Frozen(Color.FromArgb(0xD8, 0x11, 0x23, 0x16));
    private static readonly Brush BadgeBorder = Frozen(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush GreenGlow = Frozen(Color.FromRgb(0x1E, 0xD7, 0x60));

    // Text brushes
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush UnitBrush = Frozen(Color.FromRgb(0x7A, 0x88, 0x9B));
    private static readonly Brush FpsBrush = Frozen(Color.FromRgb(0x1E, 0xD7, 0x60));
    private static readonly Brush WarmBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));

    private readonly TextBlock _clock = Text(150, FontWeights.Bold, ValueBrush);
    private readonly TextBlock _date = Text(32, FontWeights.SemiBold, LabelBrush);
    private readonly (TextBlock Label, TextBlock Value, TextBlock Unit)[] _tiles = new (TextBlock, TextBlock, TextBlock)[6];
    private ScreenRect? _placed;

    public ScreenStatsWindow()
    {
        Title = "Handheld Optimiser - Stats on Handheld Screen";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Page;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Cursor = System.Windows.Input.Cursors.None;

        var page = new Grid { Width = ScreenStatsLayout.PanelWidth, Height = ScreenStatsLayout.PanelHeight, Margin = new Thickness(0) };
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition());
        for (var column = 0; column < 3; column++)
        {
            page.ColumnDefinitions.Add(new ColumnDefinition());
        }

        // The time on the left, telemetry status pill and date on the right, along the top.
        var header = new Grid { Margin = new Thickness(36, 12, 36, 14) };
        _clock.VerticalAlignment = VerticalAlignment.Center;
        _clock.Margin = new Thickness(0, -6, 0, 0);

        var rightStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pillBeacon = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = GreenGlow,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var pillText = new TextBlock
        {
            Text = "LIVE TELEMETRY",
            FontFamily = Blocky,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = GreenGlow,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
        pillContent.Children.Add(pillBeacon);
        pillContent.Children.Add(pillText);

        var pill = new Border
        {
            Background = BadgeBackground,
            BorderBrush = BadgeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8),
            Child = pillContent
        };

        _date.HorizontalAlignment = HorizontalAlignment.Right;
        _date.VerticalAlignment = VerticalAlignment.Center;

        rightStack.Children.Add(pill);
        rightStack.Children.Add(_date);

        header.Children.Add(_clock);
        header.Children.Add(rightStack);
        Grid.SetColumnSpan(header, 3);
        page.Children.Add(header);

        var divider = new Border
        {
            Height = 2,
            Margin = new Thickness(24, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = HeaderDivider
        };
        Grid.SetColumnSpan(divider, 3);
        page.Children.Add(divider);

        // Two rows of three NXE cards under it.
        for (var i = 0; i < _tiles.Length; i++)
        {
            var isHero = i == 0;
            var label = Text(24, FontWeights.Bold, isHero ? GreenGlow : LabelBrush);
            label.Margin = new Thickness(0, 0, 0, 4);

            var value = Text(136, FontWeights.Bold, isHero ? FpsBrush : ValueBrush);
            var unit = Text(44, FontWeights.SemiBold, UnitBrush);
            unit.Margin = new Thickness(8, 0, 0, 24);
            unit.VerticalAlignment = VerticalAlignment.Bottom;

            var reading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            reading.Children.Add(value);
            reading.Children.Add(unit);

            var sheen = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10),
                Background = isHero ? HeroCardSheen : CardSheen
            };

            var inside = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(sheen, Dock.Top);
            DockPanel.SetDock(label, Dock.Top);
            inside.Children.Add(sheen);
            inside.Children.Add(label);
            inside.Children.Add(reading);

            var tile = new Border
            {
                Background = isHero ? HeroCardFill : CardFill,
                BorderBrush = isHero ? HeroCardBorder : CardBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(26, 16, 20, 4),
                Margin = new Thickness(i % 3 == 0 ? 24 : 8, i < 3 ? 12 : 8, i % 3 == 2 ? 24 : 8, i < 3 ? 8 : 24),
                Child = inside
            };

            Grid.SetRow(tile, 1 + i / 3);
            Grid.SetColumn(tile, i % 3);
            page.Children.Add(tile);
            _tiles[i] = (label, value, unit);
        }

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = page };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    /// <summary>Shows this second's readings and the time.</summary>
    public void ShowReadings(IReadOnlyList<StatTile> tiles, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        _clock.Text = ScreenStatsLayout.Clock(now, culture);
        _date.Text = ScreenStatsLayout.Date(now, culture);

        for (var i = 0; i < _tiles.Length && i < tiles.Count; i++)
        {
            var (label, value, unit) = _tiles[i];
            label.Text = tiles[i].Label;
            value.Text = tiles[i].Value;
            unit.Text = tiles[i].Unit;
            value.Foreground = tiles[i].Tone switch
            {
                StatTone.Hot => HotBrush,
                StatTone.Warm => WarmBrush,
                _ => i == 0 ? FpsBrush : ValueBrush
            };
        }
    }

    /// <summary>Shows the window over this part of the desktop, in pixels: the Ally's panel.</summary>
    public void ShowOn(ScreenRect bounds)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (_placed != bounds || !IsVisible)
        {
            // Placed before it shows, so it never flashes up on the monitor first.
            SetWindowPos(handle, TopMost, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate);
            _placed = bounds;
        }

        if (!IsVisible)
        {
            Show();
        }

        // Kept on top of its screen, and where it was put, should Windows move it as the screens change.
        SetWindowPos(handle, TopMost, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | ShowWindowFlag);
    }

    /// <summary>Hides the window while there is no panel for it, as when undocked.</summary>
    public void HideAway()
    {
        if (IsVisible)
        {
            Hide();
        }

        _placed = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // It never takes the keyboard or the controller, and stays out of the taskbar and Alt+Tab.
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtrW(handle, ExStyle);
        SetWindowLongPtrW(handle, ExStyle, style | ExToolWindow | ExNoActivate);
    }

    private static TextBlock Text(double size, FontWeight weight, Brush brush) => new()
    {
        FontFamily = Blocky,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextTrimming = TextTrimming.None
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush FrozenLinearGradient(Point start, Point end, params (double offset, Color color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
