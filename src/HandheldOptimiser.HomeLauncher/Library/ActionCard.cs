using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A card that does something when it is pressed: one of the settings' lists, a power action, or the
/// way in to the games that are not installed. Green, with a white symbol, as the Xbox 360's own were.
/// </summary>
internal sealed class ActionCard(string glyph, string name, string? detail, Action act) : INotifyPropertyChanged, INoticeHost
{
    private string _title = name;
    private string? _notice;

    public event PropertyChangedEventHandler? PropertyChanged;

    // The symbols' shapes, made once each: the cards are made again whenever their category is opened.
    private static readonly Dictionary<string, Geometry> Shapes = [];

    /// <summary>A symbol from Windows' icon font.</summary>
    public string Glyph { get; } = glyph;

    /// <summary>
    /// The symbol as a shape, which is how the card draws it. As text it went missing from the cards
    /// furthest back: text that big is not drawn at all once its card is made small enough, where a
    /// shape is drawn at any size.
    /// </summary>
    public Geometry Icon => Shapes.TryGetValue(Glyph, out var shape) ? shape : Shapes[Glyph] = Outline(Glyph);

    private static Geometry Outline(string glyph)
    {
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 100, Brushes.White, 1);
        var shape = text.BuildGeometry(default);
        shape.Freeze();
        return shape;
    }

    /// <summary>What the card is, which is also what it says unless it is waiting for a second press.</summary>
    public string Name { get; } = name;

    public string? Detail { get; } = detail;

    public bool HasDetail => Detail is not null;

    public Action Act { get; } = act;

    /// <summary>What the library has to say, shown on the card in place of its name and detail.</summary>
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

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }
    }
}
