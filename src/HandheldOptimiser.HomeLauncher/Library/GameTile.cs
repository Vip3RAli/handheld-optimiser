using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What one tile shows. Built off the UI thread with frozen images, decoded at tile size so a large
/// library costs a few MB of pixels rather than the full-size artwork.
/// </summary>
internal sealed class GameTile
{
    // Tiles are 180 x 270 DIPs; 360 px wide covers up to 200% scaling.
    private const int CoverDecodeWidth = 360;
    private const int IconSize = 256;

    public required Game Game { get; init; }
    public ImageSource? Cover { get; init; }
    public ImageSource? Icon { get; init; }
    public required Brush Background { get; init; }

    public string Title => Game.Title;
    public string StoreName => Game.StoreName;
    public bool HasCover => Cover is not null;
    public bool ShowsIcon => Cover is null;

    public static GameTile Create(Game game) => new()
    {
        Game = game,
        Cover = game.CoverPath is null ? null : LoadCover(game.CoverPath),
        Icon = game.CoverPath is null && game.IconPath is not null ? Native.LoadIcon(game.IconPath, IconSize) : null,
        Background = StoreBrushes[game.Store]
    };

    private static ImageSource? LoadCover(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = CoverDecodeWidth;
            // OnLoad reads the file now and releases it, so Steam can replace its art while we are open.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static readonly Dictionary<GameStore, Brush> StoreBrushes = new()
    {
        [GameStore.Steam] = Gradient("#2A475E", "#171A21"),
        [GameStore.Epic] = Gradient("#3A3A3A", "#141414"),
        [GameStore.BattleNet] = Gradient("#1473B8", "#0A2A4A"),
        [GameStore.Gog] = Gradient("#7A2F80", "#2C1030")
    };

    private static Brush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(top),
            (Color)ColorConverter.ConvertFromString(bottom),
            90);
        brush.Freeze();
        return brush;
    }
}
