using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One line of the game details panel, such as "Time played" and "12 h 5 min".</summary>
internal sealed record InfoRow(string Label, string Value);

/// <summary>
/// What the library shows about the games beyond their pictures: which one was played last, which goes first
/// among them with a badge; in the row, the picked game's details below it (play time, achievements, size,
/// profile, and what its store says when Store Description is on); in the grid, the last game's play time and
/// achievements on its picture; and in the NXE layout, the focused card's, as its Card details setting asks.
/// Until 2026-11-16 the last game played had a wide card of its own above the games, which the user had taken
/// out for the badge.
/// </summary>
internal sealed class ContinuePresenter
{
    // The least room the panel needs, and the least for the description beside its lines.
    private const double InfoMinWidth = 280;
    private const double AboutMinWidth = 640;

    // How wide the lines are, alone and with the description beside them.
    private const double InfoWidthAlone = 440;
    private const double RowsWidth = 300;

    private readonly ContinueView _view;
    private readonly ScrollViewer _scroller;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly Func<bool> _isRow;
    private readonly Func<bool> _isOldSchool;

    // The game the panel is about, and what was found for it, so the lines are rebuilt without asking again.
    private string? _infoKey;
    private long? _infoSize;
    private GameDetails? _infoDetails;

    // The game the details below the row are for, the one they last faded in for, and the wait for the focus to
    // settle before they change, so holding a direction along the row does not look every game up on the way.
    private GameTile? _detailsFor;
    private string? _detailsShown;
    private DispatcherTimer? _settle;

    // The NXE card showing its play time or achievements, which only the focused one does.
    private GameTile? _cardDetailTile;

    // Each game's size on disk, measured once a session: walking a big game's folder takes a while.
    private readonly Dictionary<string, long> _sizes = [];

    /// <param name="scroller">The page, whose width says how much room the panel has.</param>
    public ContinuePresenter(ContinueView view, ScrollViewer scroller, LibraryState state, LibraryOptions options,
        Func<bool> isRow, Func<bool> isOldSchool)
    {
        _view = view;
        _scroller = scroller;
        _state = state;
        _options = options;
        _isRow = isRow;
        _isOldSchool = isOldSchool;
    }

    /// <summary>
    /// The last game played, which goes first among the games with a badge, when Last played first is on in this
    /// layout and the Games tab is open; null otherwise.
    /// </summary>
    public GameTile? LastPlayed() =>
        _options.ContinuePlaying && _state.Filter == TileFilter.All
            ? RecentGames.Pick(_state.Shown(_options.ShowHidden).Where(t => t.IsInstalled && !t.IsHidden && !t.IsApp),
                t => _state.StatsOf(t).LastPlayed, 1).FirstOrDefault()
            : null;

    /// <summary>Shows the details panel below the row for the game picked there, or hides it.</summary>
    public void Update()
    {
        var view = _view;
        var tile = _isRow() && _options.ContinueInfo && _state.Filter == TileFilter.All ? _detailsFor : null;
        if (tile is null || !_state.Tiles.Contains(tile))
        {
            view.ContinuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        view.ContinuePanel.Visibility = Visibility.Visible;
        UpdateInfo(tile);
        if (view.ContinueInfo.Visibility != Visibility.Visible)
        {
            view.ContinuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        // A new game's details fade in rather than swapping at once.
        if (tile.Game.Key != _detailsShown)
        {
            _detailsShown = tile.Game.Key;
            view.ContinueInfo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

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

    /// <summary>Forgets what the panel found, so it is looked for again, as after its settings changed.</summary>
    public void ForgetInfo() => _infoKey = null;

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

    // ----- The details panel -----

    /// <summary>Whether the panel has room on this screen.</summary>
    private bool InfoFits() => _options.ContinueInfo && InfoRoom() >= InfoMinWidth;

    private double InfoRoom() => _scroller.ActualWidth - _scroller.Padding.Left - _scroller.Padding.Right - 20;

    /// <summary>Fills the panel for a game, or hides it when it is off or does not fit.</summary>
    private void UpdateInfo(GameTile tile)
    {
        if (!InfoFits())
        {
            _view.ContinueInfo.Visibility = Visibility.Collapsed;
            return;
        }

        if (tile.Game.Key == _infoKey)
        {
            ShowInfo(tile);
            return;
        }

        _infoKey = tile.Game.Key;
        _infoSize = null;
        _infoDetails = _options.StoreDetails ? StoreDetails.Cached(tile.Game) : null;
        ShowInfo(tile);
        LoadInfo(tile);
    }

    /// <summary>Measures the game's folder and asks for its store details, both of which take a while.</summary>
    private async void LoadInfo(GameTile tile)
    {
        var game = tile.Game;
        var details = _options.StoreDetails && _infoDetails is null ? Task.Run(() => StoreDetails.FindAsync(game)) : null;

        // A program the player added may sit in a folder full of other things.
        if (game.Store is not (GameStore.Other or GameStore.Emulator) && game.InstallDirectory is { } folder)
        {
            if (!_sizes.TryGetValue(game.Key, out var size))
            {
                size = await Task.Run(() => Directory.Exists(folder) ? Cleanup.SizeOf([folder]) : 0);
                _sizes[game.Key] = size;
            }

            // The panel may have moved on to another game meanwhile.
            if (_infoKey != game.Key)
            {
                return;
            }

            _infoSize = size;
            ShowInfo(tile);
        }

        if (details is not null && await details is { } found && _infoKey == game.Key && _options.StoreDetails)
        {
            _infoDetails = found;
            ShowInfo(tile);
        }
    }

    private void ShowInfo(GameTile tile)
    {
        var view = _view;
        var stats = _state.StatsOf(tile);
        var rows = new List<InfoRow>();
        if (stats.Played >= TimeSpan.FromMinutes(1))
        {
            rows.Add(new InfoRow("Time played", PlayStats.Duration(stats.Played)));
        }

        if (Achievements.For(tile.Game) is { Total: > 0 } achievements)
        {
            rows.Add(new InfoRow("Achievements", achievements.Describe()));
        }

        if (_infoSize is { } size && size > 0)
        {
            rows.Add(new InfoRow("Size on disk", CleanResult.Size(size)));
        }

        var profile = GameProfiles.For(tile.Game);
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
            return;
        }

        view.ContinueRows.ItemsSource = rows;
        view.ContinueAbout.Text = about;
        view.ContinueAbout.Visibility = about is null ? Visibility.Collapsed : Visibility.Visible;

        var source = about is null ? null : _infoDetails?.Source;
        view.ContinueSource.Text = source is null ? null : $"From {source}";
        view.ContinueSource.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        // With no description the lines would be lost in a panel as wide as the screen, and as tall as one with it.
        view.ContinueRows.Width = about is null ? double.NaN : RowsWidth;
        view.ContinueInfo.Width = about is null ? Math.Min(InfoWidthAlone, InfoRoom() - 20) : double.NaN;
        view.ContinueInfo.Height = about is null ? double.NaN : 270;
        view.ContinueInfo.HorizontalAlignment = about is null ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        view.ContinueInfo.Visibility = Visibility.Visible;
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Where the last game played goes among the games, which the user set out on 2026-11-16.</summary>
internal static class HomeOrder
{
    /// <summary>
    /// The games in order with the last one played first, ahead of the favourites, in every layout. It is only
    /// listed once, so a favourite played last is not also among the favourites.
    /// </summary>
    public static List<T> Arrange<T>(List<T> games, T? lastPlayed) where T : class =>
        lastPlayed is null || !games.Contains(lastPlayed)
            ? games
            : [lastPlayed, .. games.Where(g => !ReferenceEquals(g, lastPlayed))];
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
