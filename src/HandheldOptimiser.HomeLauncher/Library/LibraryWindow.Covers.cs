namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Portrait covers for games whose store keeps none on disk: from SteamGridDB, or for a game that is not
/// installed, from its store's own image server.
/// </summary>
public partial class LibraryWindow
{
    // Few enough at once to be polite to SteamGridDB, enough that a new library fills in a minute.
    private const int CoverFetchesAtOnce = 3;

    // Games SteamGridDB has been asked about since the library opened. It stays open for days and
    // rescans every couple of minutes, so without this a game that cannot be fetched would be asked for
    // again on every scan.
    private readonly HashSet<string> _coversAsked = new(StringComparer.OrdinalIgnoreCase);
    private string? _coversAskedWith;
    private bool _fetchingCovers;
    private bool _fetchCoversAgain;

    // The API key SteamGridDB turned away, so nothing more is asked for until the player changes it.
    private string? _rejectedKey;

    // Covers for games that are not installed, fetched alongside the others as their pictures load.
    private readonly SemaphoreSlim _storeCoverSlots = new(CoverFetchesAtOnce);

    /// <summary>
    /// Gives a cover to every installed game whose store keeps none, from disk if it was fetched before and
    /// from SteamGridDB otherwise. Each tile changes as its own cover arrives; the grid is never rebuilt,
    /// so the focus and the scroll position stay where they are.
    /// </summary>
    /// <param name="notInstalled">
    /// Games that are not installed whose pictures have just loaded, to fetch covers for instead. Their
    /// stores' own covers need no SteamGridDB key.
    /// </param>
    private async Task FetchCoversAsync(IReadOnlyList<GameTile>? notInstalled = null)
    {
        if (_fetchingCovers && notInstalled is null)
        {
            // Something changed while covers were on their way; go round again once they are in.
            _fetchCoversAgain = true;
            return;
        }

        var apiKey = LibrarySettings.ArtworkKey;
        var mayAsk = apiKey is not null && apiKey != _rejectedKey;

        // A different key may get what the last one could not.
        if (apiKey != _coversAskedWith)
        {
            _coversAsked.Clear();
            _coversAskedWith = apiKey;
        }

        // A cover already on disk needs no key and no request. This also catches one that finished
        // downloading for a tile a rescan has since replaced.
        // Apps keep their own icon: SteamGridDB is for games, and would find the wrong thing for most.
        var wanted = (notInstalled ?? _allTiles.Where(t => t.IsInstalled && !t.IsApp))
            .Where(t => t.NeedsCover && (Artwork.CachedCover(t.Game) is not null
                || ((mayAsk || t.Game.CoverUrl is not null) && _coversAsked.Add(t.Game.Key))))
            .ToList();

        if (wanted.Count == 0)
        {
            return;
        }

        if (notInstalled is not null)
        {
            await FetchCoversAsync(wanted, _storeCoverSlots, apiKey);
            return;
        }

        _fetchingCovers = true;
        try
        {
            using var slots = new SemaphoreSlim(CoverFetchesAtOnce);
            await FetchCoversAsync(wanted, slots, apiKey);
        }
        finally
        {
            _fetchingCovers = false;
        }

        if (_fetchCoversAgain)
        {
            _fetchCoversAgain = false;
            _ = FetchCoversAsync();
        }
    }

    private async Task FetchCoversAsync(List<GameTile> wanted, SemaphoreSlim slots, string? apiKey)
    {
        await Task.WhenAll(wanted.Select(async tile =>
        {
            await slots.WaitAsync();
            try
            {
                if (_rejectedKey is not null && _rejectedKey == apiKey && Artwork.CachedCover(tile.Game) is null
                    && tile.Game.CoverUrl is null)
                {
                    return;
                }

                // A game that is not installed whose pictures were let go while it waited.
                if (!tile.IsLoaded)
                {
                    _coversAsked.Remove(tile.Game.Key);
                    return;
                }

                // Downloaded, decoded and its colours taken off the UI thread; only the swap happens here.
                var (cover, colours, keyRejected) = await Task.Run(async () =>
                {
                    var (path, rejected) = await Artwork.FindCoverAsync(tile.Game);
                    var image = path is null ? null : GameTile.LoadCover(path);
                    return (image, Palette.Colours(image), rejected);
                });

                if (keyRejected)
                {
                    _rejectedKey = apiKey;
                }

                if (cover is null || !tile.IsLoaded)
                {
                    return;
                }

                tile.ShowCover(cover, colours);

                // The colours behind the grid came from the icon, so take them from the cover now.
                if (tile.Game.Key == _focusedKey)
                {
                    ReloadBackdrop();
                }
            }
            finally
            {
                slots.Release();
            }
        }));
    }
}
