using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The stats screen's window: it fills the handheld's panel with the time, a row of badges (Wi-Fi, battery,
/// Battery Care) and six tiles, each an icon, a name, a big value and a thin line that fills with it. It is
/// drawn at 1280 by 720 and scaled to the panel, whatever its size and scaling. It never takes the keyboard or
/// the controller from the game, and is not in the taskbar or Alt+Tab. Built in code: dark glass, hairlines,
/// and one jade accent, the same as TDP Engine's widget.
/// </summary>
internal sealed partial class ScreenStatsWindow : Window
{
    private const int ExStyle = -20;
    private const int ExToolWindow = 0x00000080;
    private const int ExNoActivate = 0x08000000;

    private static readonly nint TopMost = -1;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindowFlag = 0x0040;

    // A hairline: thinner than a pixel of the 720p drawing, so it reads as a faint, exact rule.
    private const double Hairline = 0.5;

    // The battery's picture: how wide its inside is when full.
    private const double BatteryInside = 27;

    // Names and the clock in a geometric face, values in a fixed width one so digits do not shift as they change.
    private static readonly FontFamily Geometric = new("Bahnschrift, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static readonly Brush Page = Frozen(Color.FromRgb(0x0C, 0x0D, 0x11));
    private static readonly Brush CardFill = Frozen(Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardEdge = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Rule = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Track = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Dim = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    // The one accent, a quiet jade, for what is live and for the frame rate. Amber and red are for heat and a low battery.
    private static readonly Brush Jade = Frozen(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xF3, 0xF4, 0xF6));
    private static readonly Brush UnitBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush WarmBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));

    // The icons, each drawn in a 24 by 24 square with a thin line. One for each tile, in the tiles' order:
    // a target, a bolt, a thermometer, a chip, a pulse and a graphics card.
    private static readonly Geometry[] TileIcons =
    [
        Outline(new EllipseGeometry(new Point(12, 12), 8, 8), new EllipseGeometry(new Point(12, 12), 3, 3),
            Geometry.Parse("M12,1.5 V5 M12,19 V22.5 M1.5,12 H5 M19,12 H22.5")),
        Outline(Geometry.Parse("M13.5,2 L5,13.5 H11.5 L10.5,22 L19,10.5 H12.5 Z")),
        Outline(Geometry.Parse("M9.5,5 A2.5,2.5 0 0 1 14.5,5 V13.2 A5,5 0 1 1 9.5,13.2 Z M12,9 V15"),
            new EllipseGeometry(new Point(12, 17), 1.6, 1.6)),
        Outline(new RectangleGeometry(new Rect(5.5, 5.5, 13, 13), 2, 2), new RectangleGeometry(new Rect(9.5, 9.5, 5, 5), 0.8, 0.8),
            Geometry.Parse("M9,2 V5.5 M15,2 V5.5 M9,18.5 V22 M15,18.5 V22 M2,9 H5.5 M2,15 H5.5 M18.5,9 H22 M18.5,15 H22")),
        Outline(Geometry.Parse("M1.5,12 H7 L9.5,4.5 L14.5,19.5 L17,12 H22.5")),
        Outline(new RectangleGeometry(new Rect(4.5, 6.5, 18, 10.5), 2, 2), new EllipseGeometry(new Point(16, 11.75), 3, 3),
            Geometry.Parse("M1.5,4.5 V20 M7,20 H13 M8.5,10 V13.5"))
    ];

    private static readonly Geometry BoltIcon = TileIcons[1];
    private static readonly Geometry ShieldIcon = Outline(
        Geometry.Parse("M12,2.5 L19.5,5.5 V11 C19.5,16 16,19.8 12,21.5 C8,19.8 4.5,16 4.5,11 V5.5 Z M8.8,11.8 L11.2,14.2 L15.4,9.6"));

    private readonly TextBlock _clock = Text(Geometric, 128, FontWeights.Light, ValueBrush);
    private readonly TextBlock _period = Text(Geometric, 30, FontWeights.SemiBold, LabelBrush);
    private readonly TextBlock _date = Text(Geometric, 21, FontWeights.SemiBold, LabelBrush);

    private readonly Rectangle[] _wifiBars = new Rectangle[4];
    private readonly TextBlock _wifiName = Text(Mono, 17, FontWeights.Normal, ValueBrush);
    private readonly Border _wifiBadge;
    private readonly Border _batteryFill = new() { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Path _batteryBolt = Glyph(BoltIcon, Jade, 18);
    private readonly TextBlock _batteryText = Text(Mono, 17, FontWeights.Normal, ValueBrush);
    private readonly Border _batteryBadge;
    private readonly Border _careBadge;

    private readonly TileParts[] _tiles = new TileParts[6];
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

        _wifiBadge = Badge(WifiBars(), _wifiName);
        _batteryBadge = Badge(BatteryPicture(), _batteryBolt, _batteryText);
        _careBadge = Badge(Glyph(ShieldIcon, Jade, 20), BadgeText("CARE 80%"));

        var page = new Grid { Width = ScreenStatsLayout.PanelWidth, Height = ScreenStatsLayout.PanelHeight };
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(188) });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition());
        for (var column = 0; column < 3; column++)
        {
            page.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var header = Header();
        Grid.SetColumnSpan(header, 3);
        page.Children.Add(header);

        var rule = new Border { Height = Hairline, Margin = new Thickness(32, 0, 32, 0), VerticalAlignment = VerticalAlignment.Bottom, Background = Rule };
        Grid.SetColumnSpan(rule, 3);
        page.Children.Add(rule);

        // Two rows of three tiles under it.
        for (var i = 0; i < _tiles.Length; i++)
        {
            var tile = Tile(i);
            // The three columns are equal, so the middle tile takes the two gaps and all three come out the same width.
            tile.Margin = new Thickness(i % 3 == 0 ? 32 : i % 3 == 1 ? 16 : 0, i < 3 ? 18 : 8, i % 3 == 2 ? 32 : i % 3 == 1 ? 16 : 0, i < 3 ? 8 : 28);
            Grid.SetRow(tile, 1 + i / 3);
            Grid.SetColumn(tile, i % 3);
            page.Children.Add(tile);
        }

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = page };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    /// <summary>Shows this second's readings, the badges and the time.</summary>
    public void ShowReadings(IReadOnlyList<StatTile> tiles, StatsHeader header, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        _clock.Text = ScreenStatsLayout.ClockTime(now, culture);
        _period.Text = ScreenStatsLayout.ClockPeriod(now, culture);
        _date.Text = Spaced(ScreenStatsLayout.Date(now, culture));
        ShowBadges(header);

        for (var i = 0; i < _tiles.Length && i < tiles.Count; i++)
        {
            var parts = _tiles[i];
            var tile = tiles[i];
            var tone = tile.Tone switch
            {
                StatTone.Hot => HotBrush,
                StatTone.Warm => WarmBrush,
                _ => null
            };

            parts.Label.Text = Spaced(tile.Label);
            parts.Caption.Text = Spaced(tile.Caption);
            parts.Value.Text = tile.Value;
            parts.Unit.Text = tile.Unit;
            parts.Value.Foreground = tone ?? (i == 0 ? Jade : ValueBrush);

            // The line under the value: two columns sharing its width, the first as wide as the reading is far along.
            var fraction = tile.Fraction ?? 0;
            parts.Filled.Width = new GridLength(fraction, GridUnitType.Star);
            parts.Empty.Width = new GridLength(1 - fraction, GridUnitType.Star);
            parts.Fill.Background = tone ?? Jade;
            parts.Line.Visibility = tile.Fraction is null ? Visibility.Hidden : Visibility.Visible;
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

    /// <summary>
    /// The top of the screen: the time with its small "pm" over the date at the left, and at the right the
    /// badges over the line that says the readings are live.
    /// </summary>
    private Grid Header()
    {
        _period.VerticalAlignment = VerticalAlignment.Top;
        _period.Margin = new Thickness(10, 30, 0, 0);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 0, 0, 0) };
        time.Children.Add(_clock);
        time.Children.Add(_period);

        _date.Margin = new Thickness(2, -12, 0, 0);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(time);
        left.Children.Add(_date);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        badges.Children.Add(_wifiBadge);
        badges.Children.Add(_batteryBadge);
        badges.Children.Add(_careBadge);

        var live = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 2, 0) };
        live.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Jade, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        var liveText = Text(Geometric, 14, FontWeights.SemiBold, Jade);
        liveText.Text = Spaced("LIVE TELEMETRY");
        liveText.VerticalAlignment = VerticalAlignment.Center;
        live.Children.Add(liveText);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(badges);
        right.Children.Add(live);

        var header = new Grid { Margin = new Thickness(36, 8, 36, 8) };
        header.Children.Add(left);
        header.Children.Add(right);
        return header;
    }

    /// <summary>
    /// One tile: its icon and name, with a few small words at the right, over a hairline; the big value and
    /// its unit; and the thin line that fills with the reading. The frame rate's, the first, is in jade.
    /// </summary>
    private Border Tile(int index)
    {
        var accent = index == 0 ? Jade : LabelBrush;
        var label = Text(Geometric, 19, FontWeights.SemiBold, accent);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(11, 1, 0, 0);
        var caption = Text(Geometric, 14, FontWeights.SemiBold, UnitBrush);
        caption.HorizontalAlignment = HorizontalAlignment.Right;
        caption.VerticalAlignment = VerticalAlignment.Center;

        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(Glyph(TileIcons[index], accent, 26));
        name.Children.Add(label);
        var top = new Grid();
        top.Children.Add(name);
        top.Children.Add(caption);

        var rule = new Border { Height = Hairline, Background = Rule, Margin = new Thickness(0, 12, 0, 0) };

        var value = Text(Mono, 104, FontWeights.SemiBold, ValueBrush);
        var unit = Text(Geometric, 34, FontWeights.Normal, UnitBrush);
        unit.Margin = new Thickness(12, 0, 0, 21);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        var reading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-5, 0, 0, 0) };
        reading.Children.Add(value);
        reading.Children.Add(unit);

        var filled = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var empty = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var fill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Jade };
        var line = new Grid { Height = 3 };
        line.ColumnDefinitions.Add(filled);
        line.ColumnDefinitions.Add(empty);
        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Track };
        Grid.SetColumnSpan(track, 2);
        line.Children.Add(track);
        line.Children.Add(fill);

        var inside = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(rule, Dock.Top);
        DockPanel.SetDock(line, Dock.Bottom);
        inside.Children.Add(top);
        inside.Children.Add(rule);
        inside.Children.Add(line);
        inside.Children.Add(reading);

        // No shadow under it: on a page this dark one cannot be seen, which the player confirmed on 2026-11-11.
        _tiles[index] = new TileParts(label, caption, value, unit, line, fill, filled, empty);
        return new Border
        {
            Background = CardFill,
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(Hairline),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 16, 24, 18),
            Child = inside
        };
    }

    /// <summary>The badges as they stand now. One with nothing to say is left out: no Wi-Fi adapter, no battery, Battery Care off.</summary>
    private void ShowBadges(StatsHeader header)
    {
        _wifiBadge.Visibility = header.WifiQuality is null ? Visibility.Collapsed : Visibility.Visible;
        for (var bar = 0; bar < _wifiBars.Length; bar++)
        {
            _wifiBars[bar].Fill = bar < header.WifiBars ? ValueBrush : Dim;
        }

        _wifiName.Text = header.WifiText;
        _wifiName.Foreground = header.WifiQuality is > 0 ? ValueBrush : LabelBrush;

        _batteryBadge.Visibility = header.Battery is null ? Visibility.Collapsed : Visibility.Visible;
        _batteryFill.Width = BatteryInside * header.BatteryFraction;
        _batteryFill.Background = header.BatteryLow ? HotBrush : header.Charging ? Jade : ValueBrush;
        _batteryBolt.Visibility = header.Charging ? Visibility.Visible : Visibility.Collapsed;
        _batteryText.Text = header.Battery is { } percent ? percent.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty;

        _careBadge.Visibility = header.BatteryCare ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Four bars of rising height, lit from the left by the signal's strength.</summary>
    private StackPanel WifiBars()
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var bar = 0; bar < _wifiBars.Length; bar++)
        {
            _wifiBars[bar] = new Rectangle
            {
                Width = 4,
                Height = 6 + bar * 4,
                RadiusX = 1,
                RadiusY = 1,
                Fill = Dim,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2.5, 0)
            };
            bars.Children.Add(_wifiBars[bar]);
        }

        return bars;
    }

    /// <summary>A battery: an outline with its cap at the right, filled from the left by the charge.</summary>
    private StackPanel BatteryPicture()
    {
        _batteryFill.Height = 10;
        var body = new Border
        {
            Width = BatteryInside + 6,
            Height = 16,
            CornerRadius = new CornerRadius(3.5),
            BorderBrush = LabelBrush,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(1.8),
            Child = _batteryFill
        };
        var cap = new Border { Width = 2.5, Height = 7, CornerRadius = new CornerRadius(0, 1.5, 1.5, 0), Background = LabelBrush, Margin = new Thickness(1.5, 0, 0, 0) };

        var picture = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        picture.Children.Add(body);
        picture.Children.Add(cap);
        return picture;
    }

    /// <summary>A badge: a faint pill with a hairline edge round an icon and its words.</summary>
    private static Border Badge(params FrameworkElement[] parts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var part in parts)
        {
            part.VerticalAlignment = VerticalAlignment.Center;
            if (row.Children.Count > 0)
            {
                part.Margin = new Thickness(9, 0, 0, 0);
            }

            if (part is TextBlock words)
            {
                // A long network name is cut short rather than pushing the badges into the clock.
                words.MaxWidth = 250;
                words.TextTrimming = TextTrimming.CharacterEllipsis;
            }

            row.Children.Add(part);
        }

        return new Border
        {
            Background = CardFill,
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(Hairline),
            CornerRadius = new CornerRadius(17),
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Child = row
        };
    }

    private static TextBlock BadgeText(string text)
    {
        var words = Text(Mono, 17, FontWeights.Normal, ValueBrush);
        words.Text = text;
        return words;
    }

    /// <summary>An icon's lines, drawn at this size in this colour.</summary>
    private static Path Glyph(Geometry shape, Brush brush, double size) => new()
    {
        Data = shape,
        Stroke = brush,
        StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Several shapes as one icon. The first is always the full square, so every icon scales alike.</summary>
    private static Geometry Outline(params Geometry[] parts)
    {
        var group = new GeometryGroup();

        // Nothing to see: it only makes each icon's bounds the same 24 by 24 square.
        group.Children.Add(Geometry.Parse("M0,0 M24,24"));
        foreach (var part in parts)
        {
            group.Children.Add(part);
        }

        group.Freeze();
        return group;
    }

    /// <summary>Capitals set a little apart, with a hair space between each, as WPF has no letter spacing.</summary>
    private static string Spaced(string text) => string.Join(' ', text.ToCharArray());

    private static TextBlock Text(FontFamily family, double size, FontWeight weight, Brush brush) => new()
    {
        FontFamily = family,
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

    /// <summary>The parts of a tile that change with its reading.</summary>
    private sealed record TileParts(TextBlock Label, TextBlock Caption, TextBlock Value, TextBlock Unit, Grid Line,
        Border Fill, ColumnDefinition Filled, ColumnDefinition Empty);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
