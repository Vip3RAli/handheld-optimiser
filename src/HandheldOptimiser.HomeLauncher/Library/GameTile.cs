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
///
/// A game that is not installed starts with no pictures at all. A Steam account can own a thousand
/// games, whose covers would take hundreds of MB, so only the ones near the focus are loaded (see
/// <see cref="Load"/>) and they are let go again as the focus moves away.
/// </summary>
internal sealed class GameTile : INotifyPropertyChanged, INoticeHost
{
    // Tiles are 180 x 270 DIPs; 360 px wide covers up to 200% scaling.
    private const int CoverDecodeWidth = 360;
    private const int IconSize = 256;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required Game Game { get; init; }
    public ImageSource? Cover { get; private set; }
    public ImageSource? Icon { get; private set; }
    public required Brush Background { get; init; }

    /// <summary>
    /// The square icon for the row layout, from SteamGridDB. Null until it has been loaded, and for a game
    /// that has none, whose tile then shows its cover cut to fit.
    /// </summary>
    public ImageSource? Square { get; private set; }

    /// <summary>
    /// The wide artwork for the Old School layout's card, cut to the card's shape. Null until it has been
    /// loaded, and for a game that has none, whose card then shows its cover on <see cref="CardBrush"/>.
    /// </summary>
    public ImageSource? Wide { get; private set; }

    /// <summary>The game's own colours, behind the cover on an Old School card with no wide artwork.</summary>
    public Brush CardBrush => _cardBrush ??= Gradient(BackdropColours.From, BackdropColours.To, 45);

    private Brush? _cardBrush;

    public string Title => Game.Title;
    public string StoreName => Game.StoreName;

    /// <summary>
    /// The store's logo for the corner of the artwork, or the console's for an emulator's game. Null for
    /// an app or a program the player added.
    /// </summary>
    public Geometry? StoreLogo => Game.IsApp ? null
        : Game.Store == GameStore.Emulator ? StoreLogos.ForConsole(Game.Platform)
        : StoreLogos.For(Game.Store);

    /// <summary>False for a game the player owns but has not installed, shown faded with a download mark.</summary>
    public bool IsInstalled => Game.Installed;

    /// <summary>A program listed under Apps rather than with the games.</summary>
    public bool IsApp => Game.IsApp;

    /// <summary>Whether its pictures have been decoded. Always true for an installed game.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Starred by the player, which puts it at the front and shows a star on the tile.</summary>
    public bool IsFavourite
    {
        get => _isFavourite;
        set => Set(ref _isFavourite, value);
    }

    /// <summary>Hidden by the player, so only shown, faded, while hidden games are shown.</summary>
    public bool IsHidden
    {
        get => _isHidden;
        set => Set(ref _isHidden, value);
    }

    /// <summary>
    /// What the library has to say about the game, such as that it is starting, shown on its tile or card
    /// in place of its name. Null when it has nothing to say.
    /// </summary>
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

    private bool _isFavourite;
    private bool _isHidden;
    private string? _notice;
    public bool HasCover => Cover is not null;
    public bool ShowsIcon => Cover is null;

    /// <summary>Whether this is a game whose store keeps no cover and that has not been given one yet.</summary>
    public bool NeedsCover => Game.CoverPath is null && Cover is null;

    /// <summary>The two ends of the gradient behind the grid while this game has the focus.</summary>
    public (Color From, Color To) BackdropColours { get; private set; }

    public static GameTile Create(Game game)
    {
        if (!game.Installed)
        {
            return new() { Game = game, Background = StoreBrushes[game.Store], BackdropColours = StoreColours(game.Store) };
        }

        var (cover, icon, colours) = Pictures(game);
        return new()
        {
            Game = game,
            Cover = cover,
            Icon = icon,
            Background = StoreBrushes[game.Store],
            BackdropColours = colours,
            IsLoaded = true
        };
    }

    /// <summary>The tile's cover or icon, decoded, and the colours taken from it. Safe off the UI thread.</summary>
    public static (ImageSource? Cover, ImageSource? Icon, (Color From, Color To) Colours) Pictures(Game game)
    {
        try
        {
            // A cover fetched on an earlier run is on disk already, so the tile opens with it.
            var cover = (game.CoverPath ?? Artwork.CachedCover(game)) is { } path ? LoadCover(path) : null;
            var icon = cover is null && game.IconPath is not null ? LoadIcon(game.IconPath) : null;
            return (cover, icon, Palette.Colours(cover ?? icon) ?? StoreColours(game.Store));
        }
        catch (Exception ex) when (!LibraryApp.IsFatal(ex))
        {
            // A game's files are whatever its store left behind. One that cannot be read shows its store's
            // colours rather than failing the scan, or the batch of tiles loaded with it.
            Program.Log($"Could not load the pictures for {game.Key}: {ex.Message}");
            return (null, null, StoreColours(game.Store));
        }
    }

    /// <summary>Shows pictures from <see cref="Pictures"/> on a tile that was made without them. UI thread only.</summary>
    public void Load((ImageSource? Cover, ImageSource? Icon, (Color From, Color To) Colours) pictures)
    {
        Cover = pictures.Cover;
        Icon = pictures.Icon;
        BackdropColours = pictures.Colours;
        IsLoaded = true;
        CoverChanged();
    }

    /// <summary>Lets go of the pictures of a game that is not installed. Installed games keep theirs.</summary>
    public void Unload()
    {
        if (IsInstalled || !IsLoaded)
        {
            return;
        }

        Cover = null;
        Icon = null;
        IsLoaded = false;
        CoverChanged();
    }

    /// <summary>Lets go of the card's cached gradient brush while a game is in front.</summary>
    public void UnloadBrushes()
    {
        _cardBrush = null;
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

        return games.Select(game => kept.TryGetValue(game, out var tile) ? tile : CreateOrBare(game)).ToList();
    }

    // One game that cannot be made into a tile must not fail the whole scan: it gets a plain one instead.
    private static GameTile CreateOrBare(Game game)
    {
        try
        {
            return Create(game);
        }
        catch (Exception ex) when (!LibraryApp.IsFatal(ex))
        {
            Program.Log($"Could not make a tile for {game.Key}: {ex.Message}");
            return new() { Game = game, Background = StoreBrushes[game.Store], BackdropColours = StoreColours(game.Store), IsLoaded = game.Installed };
        }
    }

    /// <summary>
    /// Swaps the icon for a cover that has just been fetched, taking the backdrop colours from it too.
    /// Call it on the UI thread, with the picture and its colours already worked out elsewhere.
    /// </summary>
    public void ShowCover(ImageSource cover, (Color From, Color To)? colours)
    {
        Cover = cover;
        IsLoaded = true;
        BackdropColours = colours ?? BackdropColours;
        CoverChanged();
    }

    /// <summary>Shows the square icon, or with null lets go of it. UI thread only.</summary>
    public void ShowSquare(ImageSource? square)
    {
        if (!ReferenceEquals(Square, square))
        {
            Square = square;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Square)));
        }
    }

    /// <summary>Shows the wide artwork, or with null lets go of it. UI thread only.</summary>
    public void ShowWide(ImageSource? wide)
    {
        if (!ReferenceEquals(Wide, wide))
        {
            Wide = wide;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Wide)));
        }
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

    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void CoverChanged()
    {
        // The colours are taken from the cover, so the card's brush is made again from the new ones.
        _cardBrush = null;
        foreach (var name in new[] { nameof(Cover), nameof(Icon), nameof(HasCover), nameof(ShowsIcon), nameof(CardBrush) })
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
        // A corrupt or half-written file fails in the decoder as a format, COM or invalid state error
        // rather than an IO one; the same set as Backdrops.Decode, so one bad picture only costs its tile.
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException
            or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
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
        [GameStore.Ubisoft] = Gradient("#0070D1", "#061C3A"),
        [GameStore.Other] = Gradient("#4A5060", "#1A1D24"),
        [GameStore.Emulator] = Gradient("#B8336A", "#3A0F22")
    };

    // A game with no usable cover or icon takes its store's colours.
    private static (Color From, Color To) StoreColours(GameStore store)
    {
        var stops = ((LinearGradientBrush)StoreBrushes[store]).GradientStops;
        return (stops[0].Color, stops[^1].Color);
    }

    private static Brush Gradient(string top, string bottom) =>
        Gradient((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);

    private static Brush Gradient(Color from, Color to, double angle)
    {
        var brush = new LinearGradientBrush(from, to, angle);
        brush.Freeze();
        return brush;
    }
}
