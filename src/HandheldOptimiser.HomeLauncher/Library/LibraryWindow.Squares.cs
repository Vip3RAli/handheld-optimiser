namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The square icons of the row layout's tiles, from SteamGridDB. They are only held while the row is on
/// screen: loaded from disk as it is shown, asked for where there is none yet and the player has a key,
/// and let go while a game is in front. A game with none shows its cover cut to fit.
/// </summary>
public partial class LibraryWindow
{
    // Drawn at up to 300 DIPs when focused, and decoded a little over that.
    private const int SquareDecodeWidth = 320;

    // Games SteamGridDB has been asked about since the library opened, as for the covers.
    private readonly HashSet<string> _squaresAsked = new(StringComparer.OrdinalIgnoreCase);
    private string? _squaresAskedWith;
    private bool _loadingSquares;
    private bool _loadSquaresAgain;

    /// <summary>Gives the games in the row their square icons. Each tile changes as its own arrives.</summary>
    private async void LoadSquares()
    {
        if (!IsRow || !IsActive)
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
            var wanted = _tiles.Where(t => t.Square is null && t.IsInstalled).ToList();

            // The ones on disk need no key and no request.
            foreach (var tile in wanted)
            {
                var square = await Task.Run(() =>
                    Artwork.CachedSquare(tile.Game) is { } path ? GameTile.LoadCover(path, SquareDecodeWidth) : null);
                if (square is not null && IsRow && IsActive)
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

            using var slots = new SemaphoreSlim(CoverFetchesAtOnce);
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
                    if (square is not null && IsRow && IsActive)
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
    private void UnloadSquares()
    {
        foreach (var tile in _allTiles)
        {
            tile.ShowSquare(null);
        }
    }
}
