using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The stats screen's window: it fills the Ally's panel with the time and six big tiles. It is drawn at
/// 1280 by 720 and scaled to the panel, whatever its size and scaling. It never takes the keyboard or the
/// controller from the game, and is not in the taskbar or Alt+Tab. Built in code, as it is a grid of text.
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
    private static readonly Brush Page = Frozen(Color.FromRgb(0x07, 0x08, 0x0B));
    private static readonly Brush TileBrush = Frozen(Color.FromRgb(0x14, 0x17, 0x1D));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8A, 0x93, 0xA3));
    private static readonly Brush ValueBrush = Brushes.White;
    private static readonly Brush FpsBrush = Frozen(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Brush WarmBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));

    private readonly TextBlock _clock = Text(150, FontWeights.Bold, ValueBrush);
    private readonly TextBlock _date = Text(34, FontWeights.SemiBold, LabelBrush);
    private readonly (TextBlock Label, TextBlock Value, TextBlock Unit)[] _tiles = new (TextBlock, TextBlock, TextBlock)[6];
    private ScreenRect? _placed;

    public ScreenStatsWindow()
    {
        Title = "Handheld Optimiser stats screen";
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

        // The time on the left, the date on the right, along the top.
        var header = new Grid { Margin = new Thickness(40, 8, 40, 0) };
        _clock.VerticalAlignment = VerticalAlignment.Center;
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        _date.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_clock);
        header.Children.Add(_date);
        Grid.SetColumnSpan(header, 3);
        page.Children.Add(header);

        // Two rows of three tiles under it.
        for (var i = 0; i < _tiles.Length; i++)
        {
            var label = Text(26, FontWeights.SemiBold, LabelBrush);
            var value = Text(140, FontWeights.Bold, ValueBrush);
            var unit = Text(46, FontWeights.SemiBold, LabelBrush);
            unit.Margin = new Thickness(8, 0, 0, 22);
            unit.VerticalAlignment = VerticalAlignment.Bottom;

            var reading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            reading.Children.Add(value);
            reading.Children.Add(unit);

            var inside = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(label, Dock.Top);
            inside.Children.Add(label);
            inside.Children.Add(reading);

            var tile = new Border
            {
                Background = TileBrush,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(26, 16, 20, 0),
                Margin = new Thickness(i % 3 == 0 ? 24 : 8, i < 3 ? 0 : 8, i % 3 == 2 ? 24 : 8, i < 3 ? 8 : 24),
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

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
