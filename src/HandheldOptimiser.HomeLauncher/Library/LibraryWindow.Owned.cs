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

    // The game the pictures were last loaded around: the focused one, or the first on screen after the
    // grid was scrolled by touch, the mouse wheel or a search, none of which move the focus.
    private string? _picturesNearKey;

    /// <summary>Loads the pictures of the games that are not installed around one of them, and lets go of the rest.</summary>
    private void LoadNotInstalledNear(int index)
    {
        if (!_filter.NotInstalled || index < 0 || index >= _tiles.Count)
        {
            return;
        }

        _picturesNearKey = _tiles[index].Game.Key;

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
        var near = _tiles.FindIndex(t => t.Game.Key == _picturesNearKey);
        return index >= 0 && near >= 0 && index >= near - LoadedBefore && index < near + LoadedAfter;
    }

    /// <summary>
    /// After the grid scrolled: loads the pictures of the games now on screen. Moving the focus scrolls
    /// too, but so do touch and the mouse wheel, which leave the focus where it was.
    /// </summary>
    private void OnGridScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (!_filter.NotInstalled || IsRow || e.VerticalChange == 0 || _tiles.Count == 0
            || TileContainer(0) is not { ActualHeight: > 0 } first)
        {
            return;
        }

        // How many rows have gone off the top, and so the first game still on screen.
        var above = -Tiles.TranslatePoint(default, Scroller).Y;
        var row = Math.Max(0, (int)(above / first.ActualHeight));
        var columns = ColumnCount();
        var index = Math.Min(row * columns, _tiles.Count - 1);

        // Left alone while the game they were loaded around is still among the first rows on screen, as
        // the focused game is when it was the d-pad that scrolled.
        var near = _tiles.FindIndex(t => t.Game.Key == _picturesNearKey);
        if (near < index || near >= index + 3 * columns)
        {
            LoadNotInstalledNear(index);
        }
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
            if (error is not null && ReferenceEquals(_menu, OwnedItems))
            {
                Notice = error;
            }

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
        OpenSettingsGroup(OwnedItems, "Games not installed", GamesApps);
        RefreshOwnedSettings();
        Notice = _ownedError ?? string.Empty;
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
        Notice = "Looking for your games...";
        _ = RefreshOwnedNowAsync();
    }

    private async Task RefreshOwnedNowAsync()
    {
        await RefreshOwnedAsync(force: true);
        await ScanAsync();
        var count = _allTiles.Count(t => !t.IsInstalled);
        Notice = _ownedError ?? (count == 1 ? "1 game is not installed." : $"{count} games are not installed.");
    }

    private void RefreshOwnedSettings()
    {
        var on = LibrarySettings.ShowNotInstalled;
        NotInstalledSetting.Tag = OnOff(on);

        SteamKeySetting.IsEnabled = on;
        SteamKeySetting.Tag = LibrarySettings.SteamKey is null ? "Not set" : _ownedError is null ? "Set" : "Not working";

        OwnedRefreshItem.IsEnabled = on;
    }
}
