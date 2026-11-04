using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The performance overlay's window: a small dark box of rows in a corner of the screen. It is always on
/// top, never takes the keyboard, is not in the taskbar or Alt+Tab, and every click goes through it to the
/// game beneath. Built in code, as it is a few rows and nothing else.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    private const int ExStyle = -20;
    private const int ExTransparent = 0x00000020;
    private const int ExToolWindow = 0x00000080;
    private const int ExLayered = 0x00080000;
    private const int ExNoActivate = 0x08000000;

    private static readonly nint TopMost = -1;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;

    private static readonly Brush LabelBrush = CreateFrozenBrush(Color.FromRgb(0xA8, 0xB0, 0xBC));
    private static readonly Brush ValueBrush = Brushes.White;
    private static readonly Brush DividerBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0x4D, 0x5E));
    private static readonly Brush BoxBrush = CreateFrozenBrush(Color.FromArgb(0xD0, 0x10, 0x12, 0x16));
    private static readonly FontFamily DefaultFontFamily = new("Segoe UI");

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly Grid _rows = new();
    private readonly Border _box;
    private readonly List<(TextBlock Label, TextBlock Value)> _lineBlocks = [];
    private bool? _cachedHorizontal;
    private int _cachedCount;
    private string _corner = AutoTdpProtocol.TopLeft;

    public OverlayWindow()
    {
        Title = "Handheld Optimiser overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        _box = new Border
        {
            Background = BoxBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 12, 6),
            Child = _rows,
            Visibility = Visibility.Collapsed
        };

        Content = _box;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SizeChanged += (_, _) => Place();
    }

    /// <summary>
    /// Shows these rows, in this corner: one under another, or along one strip with a thin line between
    /// each name and value pair.
    /// </summary>
    public void ShowLines(IReadOnlyList<OverlayLine> lines, string corner, bool horizontal)
    {
        _corner = corner;

        if (_cachedHorizontal == horizontal && _cachedCount == lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var (lbl, val) = _lineBlocks[i];
                if (lbl.Text != lines[i].Label)
                {
                    lbl.Text = lines[i].Label;
                }
                if (val.Text != lines[i].Value)
                {
                    val.Text = lines[i].Value;
                }
            }
        }
        else
        {
            _cachedHorizontal = horizontal;
            _cachedCount = lines.Count;
            _lineBlocks.Clear();
            _rows.Children.Clear();
            _rows.RowDefinitions.Clear();
            _rows.ColumnDefinitions.Clear();

            if (horizontal)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                    {
                        AddDivider(_rows.ColumnDefinitions.Count);
                    }

                    var label = _rows.ColumnDefinitions.Count;
                    _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var lbl = AddText(lines[i].Label, 0, label, LabelBrush, FontWeights.Normal, new Thickness(0, 0, 6, 0));
                    var val = AddText(lines[i].Value, 0, label + 1, ValueBrush, FontWeights.SemiBold, default);
                    _lineBlocks.Add((lbl, val));
                }
            }
            else
            {
                _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (var i = 0; i < lines.Count; i++)
                {
                    _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var lbl = AddText(lines[i].Label, i, 0, LabelBrush, FontWeights.Normal, new Thickness(0, 0, 12, 0));
                    var val = AddText(lines[i].Value, i, 1, ValueBrush, FontWeights.SemiBold, default);
                    _lineBlocks.Add((lbl, val));
                }
            }
        }

        _box.Visibility = Visibility.Visible;
        Place();

        // A game going full screen can push other topmost windows down, so the place on top is taken again.
        if (new WindowInteropHelper(this).Handle is var handle && handle != 0)
        {
            SetWindowPos(handle, TopMost, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Clicks go through, it never takes the keyboard, and it stays out of the taskbar and Alt+Tab.
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtrW(handle, ExStyle);
        SetWindowLongPtrW(handle, ExStyle, style | ExTransparent | ExToolWindow | ExLayered | ExNoActivate);
    }

    /// <summary>A thin upright line between two pairs of the strip, in a column of its own.</summary>
    private void AddDivider(int column)
    {
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var line = new Border
        {
            Width = 1,
            Margin = new Thickness(10, 3, 10, 3),
            Background = DividerBrush
        };

        Grid.SetColumn(line, column);
        _rows.Children.Add(line);
    }

    private TextBlock AddText(string text, int row, int column, Brush brush, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = DefaultFontFamily,
            FontSize = 14,
            FontWeight = weight,
            Margin = margin,
            HorizontalAlignment = column == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        _rows.Children.Add(block);
        return block;
    }

    /// <summary>Puts the box in its corner of the main screen, the whole screen, as a game covers the taskbar.</summary>
    private void Place()
    {
        var (left, top) = OverlayLayout.Place(_corner, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight,
            ActualWidth, ActualHeight);
        Left = left;
        Top = top;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
