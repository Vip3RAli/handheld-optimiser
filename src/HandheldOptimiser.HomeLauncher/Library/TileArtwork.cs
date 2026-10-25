namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The pictures the tiles show beyond what their store keeps on disk, loaded only while they are wanted:
/// portrait covers from SteamGridDB or a store's own image server, the row layout's square icons, the
/// pictures of the games that are not installed around the focus, and the Old School cards' wide artwork.
/// Each tile changes as its own picture arrives, so the grid is never rebuilt for one.
/// </summary>
/// <param name="isRow">Whether the games are the row layout's line of square icons.</param>
/// <param name="isOldSchool">Whether the games are the Old School cards.</param>
/// <param name="cardPixels">A card's size in real pixels on this screen, which its artwork is made at.</param>
internal sealed class TileArtwork(LibraryState state, LibraryNavigation navigation, BackdropPresenter backdrop,
    Func<bool> isRow, Func<bool> isOldSchool, Func<(int Width, int Height)> cardPixels)
{
    // Few enough at once to be polite to SteamGridDB, enough that a new library fills in a minute.
    private const int FetchesAtOnce = 3;

    // Drawn at up to 300 DIPs when focused, and decoded a little over that.
    private const int SquareDecodeWidth = 320;

    // How many games either side of the focus keep their pictures, on the Not installed tab: a few screens' worth.
    private const int LoadedBefore = 24;
    private const int LoadedAfter = 48;

    // Old School cards this far beyond the ones on screen keep their artwork, so a step back does not load it again.
    private const int CardArtKept = 3;

    // The most cards that hold artwork at once, and the ones that do, with the longest unseen first.
    // One more than are wanted at a time, which is the seven on screen or sliding on and off it and
    // the three kept either side: each picture is over a megabyte, and a category gone back to loads
    // its own again in a moment.
    private const int CardArtHeld = 14;
    private readonly List<GameTile> _cardArtHeld = [];

    // Games SteamGridDB has been asked about since the library opened. It stays open for days and
    // rescans every couple of minutes, so without this a game that cannot be fetched would be asked for
    // again on every scan. The same for the square icons.
    private readonly HashSet<string> _coversAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _squaresAsked = new(StringComparer.OrdinalIgnoreCase);
    private string? _coversAskedWith;
    private string? _squaresAskedWith;
    private bool _fetchingCovers;
    private bool _fetchCoversAgain;
    private bool _loadingSquares;
    private bool _loadSquaresAgain;

    // The API key SteamGridDB turned away, so nothing more is asked for until the player changes it.
    private string? _rejectedKey;

    // Covers for games that are not installed, fetched alongside the others as their pictures load.
    private readonly SemaphoreSlim _storeCoverSlots = new(FetchesAtOnce);

    // The games whose pictures are being decoded now.
    private readonly HashSet<string> _picturesLoading = new(StringComparer.OrdinalIgnoreCase);

    // The games found to have no wide artwork since the library came to the front, so they are not asked about again.
    private readonly HashSet<string> _noCardArt = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cardArtLoad;

    /// <summary>
    /// The game the Not installed pictures were last loaded around: the focused one, or the first on screen
    /// after the grid was scrolled by touch, the mouse wheel or a search, none of which move the focus.
    /// </summary>
    public string? PicturesNearKey { get; private set; }

    /// <summary>The SteamGridDB key has changed: what the old one could not get is asked for again.</summary>
    public void KeyChanged()
    {
        _coversAsked.Clear();
        _ = FetchCoversAsync();
    }

    /// <summary>
    /// A game's artwork title has changed, so everything looked up under the old one goes: its cover, which
    /// goes back to the icon until the new one arrives, its square icon and its Old School card's picture.
    /// </summary>
    public void LookUpAgain(GameTile tile)
    {
        tile.ClearFetchedCover();
        _coversAsked.Remove(tile.Game.Key);
        _ = FetchCoversAsync();

        tile.ShowSquare(null);
        _squaresAsked.Remove(tile.Game.Key);
        LoadSquares();

        tile.ShowWide(null);
        _noCardArt.Remove(tile.Game.Key);
        LoadCardArt();
    }

    // ----- Covers -----

    /// <summary>
    /// Gives a cover to every installed game whose store keeps none, from disk if it was fetched before and
    /// from SteamGridDB otherwise.
    /// </summary>
    /// <param name="notInstalled">
    /// Games that are not installed whose pictures have just loaded, to fetch covers for instead. Their
    /// stores' own covers need no SteamGridDB key.
    /// </param>
    public async Task FetchCoversAsync(IReadOnlyList<GameTile>? notInstalled = null)
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
        // Apps are asked about too. SteamGridDB keeps programs beside its games, and Artwork only takes
        // one of those for an app, never a game that shares its name.
        var wanted = (notInstalled ?? state.AllTiles.Where(t => t.IsInstalled))
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
            using var slots = new SemaphoreSlim(FetchesAtOnce);
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
                if (tile.Game.Key == state.FocusedKey)
                {
                    backdrop.Reload();
                }
            }
            finally
            {
                slots.Release();
            }
        }));
    }

    // ----- The row layout's square icons -----

    /// <summary>
    /// Gives the games in the row their square icons: loaded from disk as the row is shown, asked for where
    /// there is none yet and the player has a key. A game with none shows its cover cut to fit.
    /// </summary>
    public async void LoadSquares()
    {
        if (!isRow() || !navigation.InFront)
        {
            return;
        }

        if (_loadingSquares)
        {
            // The games changed while icons were on their way; go round again once they are in.
            _loadSquaresAgain = true;
            return;
        }

        _loadingSquares = true;
        try
        {
            var wanted = state.Tiles.Where(t => t.Square is null && t.IsInstalled).ToList();

            // The ones on disk need no key and no request.
            foreach (var tile in wanted)
            {
                var square = await Task.Run(() =>
                    Artwork.CachedSquare(tile.Game) is { } path ? GameTile.LoadCover(path, SquareDecodeWidth) : null);
                if (square is not null && isRow() && navigation.InFront)
                {
                    tile.ShowSquare(square);
                }
            }

            var apiKey = LibrarySettings.ArtworkKey;
            if (apiKey != _squaresAskedWith)
            {
                // A different key may get what the last one could not.
                _squaresAsked.Clear();
                _squaresAskedWith = apiKey;
            }

            if (apiKey is null || apiKey == _rejectedKey)
            {
                return;
            }

            using var slots = new SemaphoreSlim(FetchesAtOnce);
            await Task.WhenAll(wanted.Where(t => t.Square is null && _squaresAsked.Add(t.Game.Key)).Select(async tile =>
            {
                await slots.WaitAsync();
                try
                {
                    if (apiKey == _rejectedKey)
                    {
                        return;
                    }

                    var (square, keyRejected) = await Task.Run(async () =>
                    {
                        var (path, rejected) = await Artwork.FindSquareAsync(tile.Game);
                        return (path is null ? null : GameTile.LoadCover(path, SquareDecodeWidth), rejected);
                    });

                    if (keyRejected)
                    {
                        _rejectedKey = apiKey;
                    }

                    // The row may have gone, or a game come to the front, while it was on its way.
                    if (square is not null && isRow() && navigation.InFront)
                    {
                        tile.ShowSquare(square);
                    }
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        finally
        {
            _loadingSquares = false;
        }

        if (_loadSquaresAgain)
        {
            _loadSquaresAgain = false;
            LoadSquares();
        }
    }

    /// <summary>Lets go of the square icons while a game is in front.</summary>
    public void UnloadSquares()
    {
        foreach (var tile in state.AllTiles)
        {
            tile.ShowSquare(null);
        }
    }

    // ----- The pictures of the games that are not installed -----

    /// <summary>Loads the pictures of the games that are not installed around one of them, and lets go of the rest.</summary>
    public void LoadNotInstalledNear(int index)
    {
        var tiles = state.Tiles;
        if (!state.Filter.NotInstalled || index < 0 || index >= tiles.Count)
        {
            return;
        }

        PicturesNearKey = tiles[index].Game.Key;

        var from = Math.Max(0, index - LoadedBefore);
        var to = Math.Min(tiles.Count, index + LoadedAfter);
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i < from || i >= to)
            {
                tiles[i].Unload();
            }
        }

        var wanted = tiles.GetRange(from, to - from)
            .Where(t => !t.IsInstalled && !t.IsLoaded && _picturesLoading.Add(t.Game.Key))
            .ToList();

        if (wanted.Count > 0)
        {
            _ = LoadPicturesAsync(wanted);
        }
    }

    private async Task LoadPicturesAsync(List<GameTile> tiles)
    {
        try
        {
            var pictures = await Task.Run(() => tiles.Select(t => (Tile: t, Pictures: GameTile.Pictures(t.Game))).ToList());
            foreach (var (tile, picture) in pictures)
            {
                // The tab may have closed, or the focus moved far away, while they were decoded.
                if (StillNearFocus(tile))
                {
                    tile.Load(picture);
                }
            }
        }
        finally
        {
            foreach (var tile in tiles)
            {
                _picturesLoading.Remove(tile.Game.Key);
            }
        }

        // The store's own cover, or SteamGridDB's, for those with none on disk yet.
        _ = FetchCoversAsync(tiles.Where(t => t.IsLoaded && t.NeedsCover).ToList());
    }

    private bool StillNearFocus(GameTile tile)
    {
        if (!state.Filter.NotInstalled || !navigation.InFront)
        {
            return false;
        }

        var index = state.Tiles.IndexOf(tile);
        var near = state.Tiles.FindIndex(t => t.Game.Key == PicturesNearKey);
        return index >= 0 && near >= 0 && index >= near - LoadedBefore && index < near + LoadedAfter;
    }

    /// <summary>Lets go of every picture of a game that is not installed.</summary>
    public void UnloadNotInstalled()
    {
        foreach (var tile in state.AllTiles)
        {
            tile.Unload();
        }
    }

    // ----- The Old School cards' wide artwork -----

    /// <summary>
    /// Gives the cards on screen their wide artwork, once the focus has settled, and lets go of the
    /// artwork of the cards far from it. Each card changes as its own arrives.
    /// </summary>
    /// <param name="atOnce">Without waiting for the focus to settle, for cards that have just come on screen.</param>
    public async void LoadCardArt(bool atOnce = false)
    {
        if (!isOldSchool() || !navigation.InFront || state.Tiles.Count == 0)
        {
            return;
        }

        _cardArtLoad?.Cancel();
        var load = _cardArtLoad = new CancellationTokenSource();
        if (!atOnce)
        {
            try
            {
                await Task.Delay(BackdropPresenter.SettleDelay, load.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var tiles = state.Tiles;
        var front = Math.Max(0, tiles.FindIndex(t => t.Game.Key == state.FocusedKey));
        var (first, last) = OldSchoolStack.Window(front, tiles.Count);

        // The cards on screen and the few either side are the ones wanted now. The rest keep their
        // artwork until there is more of it held than a couple of screens of cards, the longest unseen
        // going first, so going back to a category just left does not load it all again.
        var wanted = tiles.Skip(first - CardArtKept).Take(last - first + 1 + (2 * CardArtKept)).ToList();
        _cardArtHeld.RemoveAll(wanted.Contains);
        _cardArtHeld.RemoveAll(t => t.Wide is null);
        _cardArtHeld.AddRange(wanted);
        while (_cardArtHeld.Count > CardArtHeld)
        {
            _cardArtHeld[0].ShowWide(null);
            _cardArtHeld.RemoveAt(0);
        }

        var (width, height) = cardPixels();
        var decoded = false;

        // The front card first, then outwards from it.
        foreach (var tile in tiles.Skip(first).Take(last - first + 1).OrderBy(t => Math.Abs(tiles.IndexOf(t) - front)))
        {
            if (tile.Wide is not null || !tile.IsInstalled || _noCardArt.Contains(tile.Game.Key))
            {
                continue;
            }

            // An app has a landscape picture with its logo on it, shown whole. One with none keeps its
            // cover on its own colours: the banner artwork kept for programs is a backdrop with no logo.
            var art = await Task.Run(async () => tile.IsApp
                ? await Artwork.FindCapsuleAsync(tile.Game) is { } capsule ? Backdrops.AppCard(capsule, width, height) : null
                : await Artwork.FindAsync(tile.Game) is { } path ? Backdrops.Card(path, width, height) : null);

            // The layout may have changed or a game come to the front meanwhile.
            if (!isOldSchool() || !navigation.InFront)
            {
                break;
            }

            if (art is null)
            {
                _noCardArt.Add(tile.Game.Key);
            }
            else
            {
                tile.ShowWide(art);
                decoded = true;
            }

            // The focus has moved on, and the load that followed it takes over from here.
            if (load.IsCancellationRequested)
            {
                break;
            }
        }

        if (decoded)
        {
            // As for the background: decoding artwork leaves buffers behind that only a collection frees.
            GC.Collect();
        }
    }

    /// <summary>Lets go of the cards' artwork, while a game is in front or another layout is shown.</summary>
    public void UnloadCardArt()
    {
        _cardArtLoad?.Cancel();
        _noCardArt.Clear();
        _cardArtHeld.Clear();
        foreach (var tile in state.AllTiles)
        {
            tile.ShowWide(null);
        }
    }
}
