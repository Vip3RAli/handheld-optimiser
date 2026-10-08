using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Not installed tab: games the player owns on Steam, Epic or GOG but has not installed, with A
/// opening the store's install page. Their pictures are only loaded near the focus (see GameTile).
/// </summary>
public partial class LibraryWindow
{
    // How many games either side of the focus keep their pictures: a few screens' worth.
    private const int LoadedBefore = 24;
    private const int LoadedAfter = 48;

    // The games whose pictures are being decoded now.
    private readonly HashSet<string> _picturesLoading = new(StringComparer.OrdinalIgnoreCase);

    private bool _refreshingOwned;
    private string? _ownedError;

    /// <summary>Loads the pictures of the games that are not installed around the focus, and lets go of the rest.</summary>
    private void LoadNotInstalledNear(int index)
    {
        if (!_filter.NotInstalled || index < 0)
        {
            return;
        }

        var from = Math.Max(0, index - LoadedBefore);
        var to = Math.Min(_tiles.Count, index + LoadedAfter);
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i < from || i >= to)
            {
                _tiles[i].Unload();
            }
        }

        var wanted = _tiles.GetRange(from, to - from)
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
        if (!_filter.NotInstalled || !IsActive)
        {
            return false;
        }

        var index = _tiles.IndexOf(tile);
        var focus = _tiles.FindIndex(t => t.Game.Key == _focusedKey);
        return index >= 0 && focus >= 0 && index >= focus - LoadedBefore && index < focus + LoadedAfter;
    }

    /// <summary>Lets go of every picture of a game that is not installed.</summary>
    private void UnloadNotInstalled()
    {
        foreach (var tile in _allTiles)
        {
            tile.Unload();
        }
    }

    /// <summary>
    /// Asks Steam for the games the player owns, when they have given a key and the last answer is old,
    /// and looks at the library again if the list changed.
    /// </summary>
    private async Task RefreshOwnedAsync(bool force)
    {
        if (_refreshingOwned || !LibrarySettings.ShowNotInstalled || LibrarySettings.SteamKey is null)
        {
            return;
        }

        _refreshingOwned = true;
        try
        {
            var (changed, error) = await OwnedGames.RefreshSteamAsync(force);
            _ownedError = error;
            if (changed)
            {
                await ScanAsync();
            }
        }
        finally
        {
            _refreshingOwned = false;
        }

        if (ReferenceEquals(_menu, OwnedItems))
        {
            RefreshOwnedSettings();
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_menuTile is { } tile)
        {
            CloseMenu();
            Launch(tile);
        }
    }

    // ----- Settings: Games not installed -----

    private void OnOpenOwnedSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsGroup(OwnedItems, "Games not installed");
        RefreshOwnedSettings();
    }

    private void OnToggleNotInstalled(object sender, RoutedEventArgs e)
    {
        LibrarySettings.ShowNotInstalled = !LibrarySettings.ShowNotInstalled;
        RefreshOwnedSettings();
        _ = ScanAsync();
        _ = RefreshOwnedAsync(force: false);
    }

    private void OnEditSteamKey(object sender, RoutedEventArgs e) =>
        BeginEdit(MenuEditor.SteamKey, "Steam Web API key",
            "Free at steamcommunity.com/dev/apikey, signed in to your Steam account. Lists the Steam games you own, and is only sent to Steam. Leave empty to remove the key.",
            LibrarySettings.SteamKey);

    private void OnRefreshOwned(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Looking for your games...";
        _ = RefreshOwnedNowAsync();
    }

    private async Task RefreshOwnedNowAsync()
    {
        await RefreshOwnedAsync(force: true);
        await ScanAsync();
        var count = _allTiles.Count(t => !t.IsInstalled);
        StatusText.Text = _ownedError ?? (count == 1 ? "1 game is not installed." : $"{count} games are not installed.");
    }

    private void RefreshOwnedSettings()
    {
        var on = LibrarySettings.ShowNotInstalled;
        var count = _allTiles.Count(t => !t.IsInstalled);
        NotInstalledSetting.Tag = !on ? "Off"
            : count == 0 ? "On. None found yet: Epic and GOG Galaxy are read from this device, Steam needs a key"
            : $"On, in the Not installed tab. {count} found";

        var steamCount = OwnedGames.SteamCount();
        SteamKeySetting.IsEnabled = on;
        SteamKeySetting.Tag = LibrarySettings.SteamKey is not { } key ? "Not set. Free at steamcommunity.com/dev/apikey"
            : _ownedError is { } error ? error
            : steamCount is { } games ? $"Set, ending in {key[^Math.Min(4, key.Length)..]}. Steam listed {games} games"
            : $"Set, ending in {key[^Math.Min(4, key.Length)..]}";

        OwnedRefreshItem.IsEnabled = on;
    }
}
