namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the library shows, which every part of it reads: the games, the tab and the tiles on it, the game
/// the focus is on, and how long each game has been played. It holds no controls, so the rules about it
/// can be tested without a window.
/// </summary>
internal sealed class LibraryState
{
    /// <summary>Every game and app the last scan found, the hidden ones and those not installed included.</summary>
    public List<GameTile> AllTiles { get; set; } = [];

    /// <summary>The tiles on screen: the open tab's, in the chosen order.</summary>
    public List<GameTile> Tiles { get; set; } = [];

    public TileFilter Filter { get; set; } = TileFilter.All;

    /// <summary>The game the focus is on, or was last on, by its key. Kept across a rescan, which makes new tiles.</summary>
    public string? FocusedKey { get; set; }

    /// <summary>Each game's play time and last play, for sorting and the quick actions menu.</summary>
    public Dictionary<string, PlayStats> Stats { get; set; } = [];

    /// <summary>When the games were last scanned, or <see cref="DateTime.MinValue"/> before the first scan is in.</summary>
    public DateTime LastScan { get; set; } = DateTime.MinValue;

    /// <summary>Whether the first scan is in.</summary>
    public bool Scanned => LastScan != DateTime.MinValue;

    /// <summary>The games the library can show: all but the hidden ones, unless those are shown too.</summary>
    public IEnumerable<GameTile> Shown(bool showHidden) => showHidden ? AllTiles : AllTiles.Where(t => !t.IsHidden);

    /// <summary>Where the focused game is among the tiles on screen, or -1 when it is not there.</summary>
    public int FocusedIndex => Tiles.FindIndex(t => t.Game.Key == FocusedKey);

    /// <summary>The focused game's tile on screen, or null when it is not there.</summary>
    public GameTile? FocusedTile => Tiles.Find(t => t.Game.Key == FocusedKey);

    /// <summary>Puts one tile in place of another, as when a program moves between the games and Apps.</summary>
    public void Replace(GameTile old, GameTile replacement) =>
        AllTiles = AllTiles.Select(t => ReferenceEquals(t, old) ? replacement : t).ToList();

    /// <summary>Takes a tile out of the library, as when a program the player added is removed.</summary>
    public void Remove(GameTile tile) => AllTiles = AllTiles.Where(t => !ReferenceEquals(t, tile)).ToList();

    /// <summary>The play stats of a game, or empty ones for a game never played.</summary>
    public PlayStats StatsOf(GameTile tile) => Stats.GetValueOrDefault(tile.Game.Key);

    /// <summary>Tiles in the player's chosen order. Ties keep the order they came in: last launched, then by name.</summary>
    public IEnumerable<GameTile> Sorted(IEnumerable<GameTile> tiles, SortOrder sort) => sort switch
    {
        SortOrder.MostPlayed => tiles.OrderByDescending(t => StatsOf(t).Played),
        SortOrder.Name => tiles.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => tiles.OrderByDescending(t => StatsOf(t).LastPlayed ?? DateTimeOffset.MinValue)
    };

    /// <summary>Whether two readings of the play times say the same, so the tiles need not be drawn again.</summary>
    public static bool SameStats(Dictionary<string, PlayStats> a, Dictionary<string, PlayStats> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);
}
