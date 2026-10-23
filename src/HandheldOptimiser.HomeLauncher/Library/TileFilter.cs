namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A tab of the filter strip: every installed game, the favourites, one store's games, the games owned but
/// not installed, or the apps. Apps are only ever on their own tab.
/// </summary>
internal readonly record struct TileFilter(GameStore? Store, bool Favourites, bool NotInstalled = false, bool Apps = false)
{
    public static readonly TileFilter All = default;
    public static readonly TileFilter Starred = new(null, true);
    public static readonly TileFilter Uninstalled = new(null, false, true);
    public static readonly TileFilter AppsTab = new(null, false, Apps: true);

    public string Name => Apps ? "Apps" : NotInstalled ? "Not installed" : Favourites ? "Favourites"
        : Store is { } store ? Game.NameOf(store) : "Games";

    /// <summary>What A does to a tile on this tab.</summary>
    public string Accept => NotInstalled ? "Install" : Apps ? "Open" : "Play";

    public bool Shows(GameTile tile) => Apps ? tile.IsApp
        : NotInstalled ? !tile.IsInstalled
        : tile.IsInstalled && !tile.IsApp && (Favourites ? tile.IsFavourite : Store is not { } store || tile.Game.Store == store);

    /// <summary>
    /// The tabs of the filter strip, in order: Games; Apps, always, so there is somewhere to add them; then
    /// Favourites once a game is starred and a tab per store when games come from more than one, while the
    /// store filter is on; and last Not installed, whenever there are owned games that are not installed.
    /// </summary>
    /// <param name="shown">The games the library shows, the hidden ones left out unless they are shown too.</param>
    /// <param name="storeTabs">Whether the player switched the store filter on.</param>
    public static List<TileFilter> Options(IEnumerable<GameTile> shown, bool storeTabs)
    {
        var games = shown.Where(t => !t.IsApp).ToList();
        var installed = games.Where(t => t.IsInstalled).ToList();
        var options = new List<TileFilter> { All, AppsTab };

        if (storeTabs)
        {
            if (installed.Any(t => t.IsFavourite))
            {
                options.Add(Starred);
            }

            var stores = installed.Select(t => t.Game.Store).Distinct().Order().ToList();
            if (stores.Count > 1)
            {
                options.AddRange(stores.Select(store => new TileFilter(store, false)));
            }
        }

        if (installed.Count < games.Count)
        {
            options.Add(Uninstalled);
        }

        return options;
    }
}
