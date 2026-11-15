using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One line of the panel beside the Last played card, such as "Time played" and "12 h 5 min".</summary>
internal sealed record InfoRow(string Label, string Value);

/// <summary>
/// Last played: the last game played as a wide card above the grid, so it is a press away whatever order
/// the grid is in, and beside it the game's play time, size on disk and profile, and what its store says
/// about it when the player switched Store details on.
/// </summary>
internal sealed class ContinuePresenter
{
    // The card's artwork is decoded at about the card's width at 200% scaling.
    private const int ArtWidth = 1680;

    // The space between the card and the games in DIPs, and the most it grows to on a TV or monitor.
    private const double AllGamesGap = 16;
    private const double AllGamesMaxGap = 240;

    // A row of tiles with its titles, until the tiles have been laid out.
    private const double TileRowHeight = 317;

    // The card in DIPs including its margins, as ContinueView.xaml lays it out.
    private const double CardWidth = 580;

    // The least room the rows need beside the card, and the least for the description beside the rows.
    private const double InfoMinWidth = 280;
    private const double AboutMinWidth = 640;

    // How wide the rows are, alone and with the description beside them.
    private const double InfoWidthAlone = 440;
    private const double RowsWidth = 300;

    private readonly ContinueView _view;
    private readonly ScrollViewer _scroller;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly LibraryNavigation _navigation;
    private readonly Func<bool> _isRow;
    private readonly Func<bool> _isOldSchool;
    private readonly Func<bool> _docked;
    private readonly Func<GameTile, bool> _isPaused;
    private readonly Func<FrameworkElement?> _firstTile;

    // The game whose artwork is on the card, or on its way there.
    private string? _artKey;

    // The game the panel is about, and what was found for it, so the rows are rebuilt without asking again.
    private string? _infoKey;
    private long? _infoSize;
    private GameDetails? _infoDetails;

    /// <param name="scroller">The page, whose size says how much room there is beside the card.</param>
    /// <param name="docked">Whether the library is on a TV or monitor, where the grid is moved down to the bottom.</param>
    /// <param name="isPaused">Whether a game is the one Quick Resume has paused.</param>
    /// <param name="firstTile">The first game's tile once it is laid out, to measure a row of them by.</param>
    public ContinuePresenter(ContinueView view, ScrollViewer scroller, LibraryState state, LibraryOptions options,
        LibraryNavigation navigation, Func<bool> isRow, Func<bool> isOldSchool, Func<bool> docked, Func<GameTile, bool> isPaused,
        Func<FrameworkElement?> firstTile)
    {
        _view = view;
        _scroller = scroller;
        _state = state;
        _options = options;
        _navigation = navigation;
        _isRow = isRow;
        _isOldSchool = isOldSchool;
        _docked = docked;
        _isPaused = isPaused;
        _firstTile = firstTile;
    }

    /// <summary>Whether the focus was on the card rather than the grid, to put it back there.</summary>
    public bool FocusWasHere { get; set; }

    private bool Showing => _view.ContinuePanel.Visibility == Visibility.Visible;

    // How big the card and the panel are drawn against how ContinueView.xaml lays them out: three quarters
    // above the grid, and as they are above the row. The widths above are before that. Above the grid they
    // were half as big again until the user asked on 2026-11-16 for them halved, since with the deals at
    // the bottom they left room for only part of a row of games.
    private double Scale => _isRow() ? 1 : 0.75;

    /// <summary>
    /// The game for Jump back in, the last one played, when it is switched on in this layout and the Games tab
    /// is open; null otherwise. In the NXE layout it is the first of the games' cards rather than a card of its own.
    /// </summary>
    public GameTile? LastPlayed() =>
        _options.ContinuePlaying && _state.Filter == TileFilter.All
            ? RecentGames.Pick(_state.Shown(_options.ShowHidden).Where(t => t.IsInstalled && !t.IsHidden && !t.IsApp),
                t => _state.StatsOf(t).LastPlayed, 1).FirstOrDefault()
            : null;

    /// <summary>
    /// Shows the details panel below the row for the game picked there, or hides it. The Jump back in card above
    /// the games is gone since the user asked on 2026-11-16: the last game played is the first of the games
    /// instead, with a badge, and in the row its details, or any game's, are shown below it when Game Details is on.
    /// </summary>
    public void Update()
    {
        var view = _view;
        view.ContinueCard.Visibility = Visibility.Collapsed;
        view.ContinueHeading.Visibility = Visibility.Collapsed;
        view.AllGamesHeading.Visibility = Visibility.Collapsed;
        FocusWasHere = false;

        var tile = _isRow() && _options.ContinueInfo && _state.Filter == TileFilter.All ? _detailsFor : null;
        if (tile is null || !_state.Tiles.Contains(tile))
        {
            view.ContinuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        view.ContinueZoom.ScaleX = view.ContinueZoom.ScaleY = Scale;
        view.ContinueRow.HorizontalAlignment = HorizontalAlignment.Left;
        view.ContinuePanel.Visibility = Visibility.Visible;
        UpdateInfo(tile);
        if (view.ContinueInfo.Visibility != Visibility.Visible)
        {
            view.ContinuePanel.Visibility = Visibility.Collapsed;
        }
    }

    // The game the details below the row are for, and the wait for the focus to settle before they change, so
    // holding a direction along the row does not look every game up on the way.
    private GameTile? _detailsFor;
    private DispatcherTimer? _settle;

    // The NXE card showing its play time or achievements, which only the focused one does.
    private GameTile? _cardDetailTile;

    // Each game's size on disk, measured once a session: walking a big game's folder takes a while.
    private readonly Dictionary<string, long> _sizes = [];

    /// <summary>A game's tile has the focus: its details below the row, or its play time on its NXE card.</summary>
    public void Focused(GameTile tile)
    {
        if (_isOldSchool())
        {
            ShowCardDetail(tile);
            return;
        }

        if (!_isRow() || !_options.ContinueInfo)
        {
            return;
        }

        _detailsFor = tile;
        if (_settle is null)
        {
            _settle = new DispatcherTimer { Interval = BackdropPresenter.SettleDelay };
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                Update();
            };
        }

        _settle.Stop();
        _settle.Start();
    }

    /// <summary>The NXE layout's focused card shows its play time or achievements, as its Card details setting asks.</summary>
    private void ShowCardDetail(GameTile tile)
    {
        if (_cardDetailTile is { } before && !ReferenceEquals(before, tile))
        {
            before.CoverDetail = null;
        }

        var details = LibrarySettings.CardDetails;
        tile.CoverDetail = details == CardDetails.Off ? null
            : CoverDetailOf(tile, details is CardDetails.Playtime or CardDetails.Both, details is CardDetails.Achievements or CardDetails.Both);
        _cardDetailTile = tile;
    }

    /// <summary>
    /// What the games show on their pictures: in the grid the last game played its play time and achievements,
    /// with Game Details on; in the NXE layout only the focused card, which <see cref="Focused"/> looks after.
    /// </summary>
    public void UpdateCoverDetails(IEnumerable<GameTile> tiles, GameTile? lastPlayed)
    {
        var grid = !_isRow() && !_isOldSchool() && _options.ContinueInfo;
        foreach (var tile in tiles)
        {
            tile.CoverDetail = _isOldSchool() ? (ReferenceEquals(tile, _cardDetailTile) ? tile.CoverDetail : null)
                : grid && ReferenceEquals(tile, lastPlayed) ? CoverDetailOf(tile, playtime: true, achievements: true)
                : null;
        }
    }

    /// <summary>A game's play time and achievements as lines on its picture, or null when it has neither.</summary>
    private string? CoverDetailOf(GameTile tile, bool playtime, bool achievements)
    {
        var lines = new List<string>();
        if (playtime && _state.StatsOf(tile).Played is var played && played >= TimeSpan.FromMinutes(1))
        {
            lines.Add($"{PlayStats.Duration(played)} played");
        }

        if (achievements && Achievements.For(tile.Game) is { Total: > 0 } unlocked)
        {
            lines.Add($"Achievements {unlocked.Describe()}");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// On a TV or monitor, moves the grid down so its last whole row on show sits at the bottom of the screen, when the
    /// screen has room to spare below it. On the built-in screen the gap stays as it is.
    /// </summary>
    private void PlaceAllGames()
    {
        if (!Showing || _isRow())
        {
            return;
        }

        var view = _view;
        var gap = AllGamesGap;
        // Not with the deals at the bottom of the screen (the page then ends above them, DealsPresenter.FitPage):
        // the gap pushed the games down out of sight there, which the user saw on 2026-11-16.
        var dealsBelow = _scroller.Margin.Bottom > 0;
        if (_docked() && !dealsBelow && _scroller.ViewportHeight > 0)
        {
            var row = _firstTile() is { ActualHeight: > 0 } tile ? tile.ActualHeight : TileRowHeight;
            var above = view.ContinuePanel.ActualHeight - view.AllGamesHeading.Margin.Top;

            // As many whole rows as fit below the card, and the room left over above the heading, so the
            // last of them ends at the bottom. It used to be one row whatever the room, which pushed the
            // second row out of sight once the card was made smaller (2026-11-16).
            var room = _scroller.ViewportHeight - above;
            var rows = Math.Max(1, Math.Floor((room - AllGamesGap) / row));
            gap = Math.Clamp(room - rows * row, AllGamesGap, AllGamesMaxGap);
        }

        var margin = view.AllGamesHeading.Margin;
        if (Math.Abs(margin.Top - gap) >= 1)
        {
            view.AllGamesHeading.Margin = new Thickness(margin.Left, gap, margin.Right, margin.Bottom);
        }
    }

    /// <summary>Puts the game's wide artwork on the card, or its colours when it has none.</summary>
    public async void ShowArt()
    {
        var view = _view;
        if (!Showing || view.ContinueCard.DataContext is not GameTile tile || tile.Game.Key == _artKey || !_navigation.InFront)
        {
            return;
        }

        _artKey = tile.Game.Key;
        var (from, to) = tile.BackdropColours;
        var colours = new LinearGradientBrush(from, to, 0);
        colours.Freeze();
        view.ContinueBack.Background = colours;
        view.ContinueArt.Source = null;

        // async void, so nothing would catch a failure here but the dispatcher: the card just keeps its colours.
        ImageSource? art;
        try
        {
            art = await Task.Run(async () =>
                await Artwork.FindAsync(tile.Game) is { } path ? GameTile.LoadCover(path, ArtWidth) : null);
        }
        catch (Exception ex)
        {
            Program.Log($"Could not load the Last played artwork for {tile.Game.Key}: {ex.Message}");
            return;
        }

        // The card may have moved on to another game, or the library to the background, meanwhile.
        if (_artKey == tile.Game.Key)
        {
            view.ContinueArt.Source = art;
        }
    }

    /// <summary>Lets go of the card's artwork while a game is in front.</summary>
    public void ClearArt()
    {
        _artKey = null;
        _view.ContinueArt.Source = null;
    }

    /// <summary>Forgets what the panel found, so it is looked for again, as after its settings changed.</summary>
    public void ForgetInfo() => _infoKey = null;

    /// <summary>Whether an element is the card, or part of the row it is in, while the row is showing.</summary>
    public bool Contains(DependencyObject element) => Showing && _view.ContinuePanel.IsAncestorOf(element);

    /// <summary>Gives the card the focus, when the row is showing.</summary>
    /// <returns>False when there is no row to go to.</returns>
    public bool Focus() => false;

    /// <summary>
    /// Back on the card, when the focus was last on it and the game it shows is still the focused one.
    /// </summary>
    /// <returns>The game on the card when the focus went back to it, otherwise null.</returns>
    public GameTile? RestoreFocus()
    {
        // The card is gone (see Update), so there is never a card to go back to.
        if (!FocusWasHere || !Showing || _view.ContinueCard.Visibility != Visibility.Visible
            || _view.ContinueCard.DataContext is not GameTile card || card.Game.Key != _state.FocusedKey)
        {
            return null;
        }

        _view.ContinueCard.Focus();
        return card;
    }

    // ----- The panel beside the card -----

    /// <summary>Whether the panel has room beside the card on this screen.</summary>
    private bool InfoFits() => _options.ContinueInfo && InfoRoom() >= InfoMinWidth;

    // The panel has the width of the page to itself now there is no card beside it.
    private double InfoRoom() =>
        (_scroller.ActualWidth - _scroller.Padding.Left - _scroller.Padding.Right) / Scale - 20;

    /// <summary>Fills the panel for the game on the card, or hides it when it is off or does not fit.</summary>
    private void UpdateInfo(GameTile card)
    {
        if (!InfoFits())
        {
            _view.ContinueInfo.Visibility = Visibility.Collapsed;
            return;
        }

        if (card.Game.Key == _infoKey)
        {
            ShowInfo(card);
            return;
        }

        _infoKey = card.Game.Key;
        _infoSize = null;
        _infoDetails = _options.StoreDetails ? StoreDetails.Cached(card.Game) : null;
        ShowInfo(card);
        LoadInfo(card);
    }

    /// <summary>Measures the game's folder and asks for its store details, both of which take a while.</summary>
    private async void LoadInfo(GameTile card)
    {
        var game = card.Game;
        var details = _options.StoreDetails && _infoDetails is null ? Task.Run(() => StoreDetails.FindAsync(game)) : null;

        // A program the player added may sit in a folder full of other things.
        if (game.Store is not (GameStore.Other or GameStore.Emulator) && game.InstallDirectory is { } folder)
        {
            if (!_sizes.TryGetValue(game.Key, out var size))
            {
                size = await Task.Run(() => Directory.Exists(folder) ? Cleanup.SizeOf([folder]) : 0);
                _sizes[game.Key] = size;
            }

            // The card may have moved on to another game meanwhile.
            if (_infoKey != game.Key)
            {
                return;
            }

            _infoSize = size;
            ShowInfo(card);
        }

        if (details is not null && await details is { } found && _infoKey == game.Key && _options.StoreDetails)
        {
            _infoDetails = found;
            ShowInfo(card);
        }
    }

    private void ShowInfo(GameTile card)
    {
        var view = _view;
        var stats = _state.StatsOf(card);
        var rows = new List<InfoRow>();
        if (stats.Played >= TimeSpan.FromMinutes(1))
        {
            rows.Add(new InfoRow("Time played", PlayStats.Duration(stats.Played)));
        }

        if (Achievements.For(card.Game) is { Total: > 0 } achievements)
        {
            rows.Add(new InfoRow("Achievements", achievements.Describe()));
        }

        if (_infoSize is { } size && size > 0)
        {
            rows.Add(new InfoRow("Size on disk", CleanResult.Size(size)));
        }

        var profile = GameProfiles.For(card.Game);
        if (!profile.IsEmpty || profile.CloseApps is not null || profile.AutoTdpFps is not null || profile.FanCurve is not null)
        {
            rows.Add(new InfoRow("Game profile", Capitalised(profile.Describe())));
        }

        if (_infoDetails is { } details)
        {
            if (details.Genres.Length > 0)
            {
                rows.Add(new InfoRow("Genre", string.Join(", ", details.Genres.Take(3))));
            }

            if (details.Released is { } released)
            {
                rows.Add(new InfoRow("Released", released));
            }

            if (details.Developer is { } developer)
            {
                rows.Add(new InfoRow("Developer", developer));
            }
        }

        var about = InfoRoom() >= AboutMinWidth ? _infoDetails?.Description : null;
        if (about is null && rows.Count == 0)
        {
            view.ContinueInfo.Visibility = Visibility.Collapsed;
            ShowDetail(card);
            return;
        }

        view.ContinueRows.ItemsSource = rows;
        view.ContinueAbout.Text = about;
        view.ContinueAbout.Visibility = about is null ? Visibility.Collapsed : Visibility.Visible;

        var source = about is null ? null : _infoDetails?.Source;
        view.ContinueSource.Text = source is null ? null : $"From {source}";
        view.ContinueSource.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        // With no description the rows would be lost in a panel as wide as the screen.
        view.ContinueRows.Width = about is null ? double.NaN : RowsWidth;
        view.ContinueInfo.Width = about is null ? Math.Min(InfoWidthAlone, InfoRoom() - 20) : double.NaN;

        // Only as tall as its lines without a description, which the panel was otherwise mostly empty around.
        view.ContinueInfo.Height = about is null ? double.NaN : 270;
        view.ContinueInfo.HorizontalAlignment = about is null ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        view.ContinueInfo.Visibility = Visibility.Visible;
        ShowDetail(card);
    }

    /// <summary>
    /// The lines on the card under the game's name: that it is paused, or when it was played and its
    /// achievements, which are in the panel beside the card instead when that is showing.
    /// </summary>
    private void ShowDetail(GameTile card)
    {
        var view = _view;
        string? text;
        if (_isPaused(card))
        {
            text = "Paused. Press A to carry on where you left off";
        }
        else if (view.ContinueInfo.Visibility == Visibility.Visible)
        {
            text = null;
        }
        else
        {
            var lines = new List<string>();
            if (_state.StatsOf(card).Describe(DateTimeOffset.UtcNow) is { Length: > 0 } played)
            {
                lines.Add(played);
            }

            if (_options.ContinueInfo && Achievements.For(card.Game) is { Total: > 0 } achievements)
            {
                lines.Add($"Achievements {achievements.Describe()}");
            }

            text = lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }

        view.ContinueDetail.Text = text;
        view.ContinueDetail.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>The games for the Last played row.</summary>
/// <summary>Where the game on Jump back in goes among the games, which the user set out on 2026-11-16.</summary>
internal static class HomeOrder
{
    /// <summary>
    /// The games in order with the last one played placed: first of all in the NXE layout, whose Jump back in
    /// is the front card; and in the grid and the row, left out when it is a favourite, since its card above
    /// already shows it with its star. Otherwise as they are.
    /// </summary>
    public static List<T> Arrange<T>(List<T> games, T? lastPlayed, bool cards, Func<T, bool> isFavourite) where T : class
    {
        if (lastPlayed is null || !games.Contains(lastPlayed))
        {
            return games;
        }

        if (cards)
        {
            return [lastPlayed, .. games.Where(g => !ReferenceEquals(g, lastPlayed))];
        }

        return isFavourite(lastPlayed) ? games.Where(g => !ReferenceEquals(g, lastPlayed)).ToList() : games;
    }
}

internal static class RecentGames
{
    /// <summary>The games played, most recent first, up to <paramref name="count"/> of them.</summary>
    public static List<T> Pick<T>(IEnumerable<T> games, Func<T, DateTimeOffset?> lastPlayed, int count) =>
        games.Select(game => (Game: game, Last: lastPlayed(game)))
            .Where(g => g.Last is not null)
            .OrderByDescending(g => g.Last)
            .Take(Math.Max(count, 0))
            .Select(g => g.Game)
            .ToList();
}
