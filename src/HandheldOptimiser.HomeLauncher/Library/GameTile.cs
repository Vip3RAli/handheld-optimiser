using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What one tile shows. Built off the UI thread with frozen images, decoded at tile size so a large
/// library costs a few MB of pixels rather than the full-size artwork.
///
/// Only the cover ever changes afterwards: a game whose store keeps none starts on its icon and takes
/// the cover fetched for it when that arrives, without the grid being rebuilt around it.
/// </summary>
internal sealed class GameTile : INotifyPropertyChanged
{
    // Tiles are 180 x 270 DIPs; 360 px wide covers up to 200% scaling.
    private const int CoverDecodeWidth = 360;
    private const int IconSize = 256;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required Game Game { get; init; }
    public ImageSource? Cover { get; private set; }
    public ImageSource? Icon { get; private set; }
    public required Brush Background { get; init; }

    public string Title => Game.Title;
    public string StoreName => Game.StoreName;
    public bool HasCover => Cover is not null;
    public bool ShowsIcon => Cover is null;

    /// <summary>Whether this is a game whose store keeps no cover and that has not been given one yet.</summary>
    public bool NeedsCover => Game.CoverPath is null && Cover is null;

    /// <summary>The two ends of the gradient behind the grid while this game has the focus.</summary>
    public (Color From, Color To) BackdropColours { get; private set; }

    public static GameTile Create(Game game)
    {
        // A cover fetched on an earlier run is on disk already, so the tile opens with it.
        var cover = (game.CoverPath ?? Artwork.CachedCover(game)) is { } path ? LoadCover(path) : null;
        var icon = cover is null && game.IconPath is not null ? LoadIcon(game.IconPath) : null;

        return new()
        {
            Game = game,
            Cover = cover,
            Icon = icon,
            Background = StoreBrushes[game.Store],
            BackdropColours = Palette.Colours(cover ?? icon) ?? StoreColours(game.Store)
        };
    }

    /// <summary>
    /// The tiles for a fresh scan. A game that is exactly as it was last time keeps its tile, so a rescan
    /// that finds nothing new decodes no pictures at all; only new or changed games get a new tile. The
    /// library rescans every time it comes back from a game, and decoding every cover each time cost tens
    /// of MB and up to a second of CPU on a large library.
    /// </summary>
    public static List<GameTile> ForScan(IEnumerable<Game> games, IReadOnlyList<GameTile> previous)
    {
        var kept = new Dictionary<Game, GameTile>();
        foreach (var tile in previous)
        {
            kept.TryAdd(tile.Game, tile);
        }

        return games.Select(game => kept.TryGetValue(game, out var tile) ? tile : Create(game)).ToList();
    }

    /// <summary>
    /// Swaps the icon for a cover that has just been fetched, taking the backdrop colours from it too.
    /// Call it on the UI thread, with the picture and its colours already worked out elsewhere.
    /// </summary>
    public void ShowCover(ImageSource cover, (Color From, Color To)? colours)
    {
        Cover = cover;
        BackdropColours = colours ?? BackdropColours;
        CoverChanged();
    }

    /// <summary>Goes back to the icon, for a fetched cover that turned out to be the wrong game's.</summary>
    public void ClearFetchedCover()
    {
        if (Game.CoverPath is not null || Cover is null)
        {
            return;
        }

        Cover = null;
        Icon ??= Game.IconPath is not null ? LoadIcon(Game.IconPath) : null;
        BackdropColours = Palette.Colours(Icon) ?? StoreColours(Game.Store);
        CoverChanged();
    }

    private void CoverChanged()
    {
        foreach (var name in new[] { nameof(Cover), nameof(Icon), nameof(HasCover), nameof(ShowsIcon) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // Xbox games ship their logo as a png rather than inside an exe or .ico.
    private static ImageSource? LoadIcon(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? LoadCover(path, decodeWidth: 0)
            : Native.LoadIcon(path, IconSize);

    public static ImageSource? LoadCover(string path, int decodeWidth = CoverDecodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = decodeWidth;
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
        [GameStore.Xbox] = Gradient("#107C10", "#0A2E0A"),
        [GameStore.Epic] = Gradient("#3A3A3A", "#141414"),
        [GameStore.BattleNet] = Gradient("#1473B8", "#0A2A4A"),
        [GameStore.Gog] = Gradient("#7A2F80", "#2C1030"),
        [GameStore.Ea] = Gradient("#E0442E", "#4A1410"),
        [GameStore.Ubisoft] = Gradient("#0070D1", "#061C3A")
    };

    // A game with no usable cover or icon takes its store's colours.
    private static (Color From, Color To) StoreColours(GameStore store)
    {
        var stops = ((LinearGradientBrush)StoreBrushes[store]).GradientStops;
        return (stops[0].Color, stops[^1].Color);
    }

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
