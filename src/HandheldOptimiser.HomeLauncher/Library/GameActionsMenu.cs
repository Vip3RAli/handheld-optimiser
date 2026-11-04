using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A game's quick actions, from X or a press and hold on its tile: install it, star it, its profile and its
/// optimised settings, its launch arguments, folder and properties, the title its artwork is looked up by,
/// closing it while it runs, and hiding it. A program the player added can also move between the games and
/// Apps, or leave the library.
/// </summary>
internal sealed class GameActionsMenu
{
    // Two wordings on purpose. Don't change leaves Windows as it is, while Use default falls back to the
    // library's own setting for TDP Engine and the fan curve.
    private const string Unchanged = "Don't change";
    private const string UseDefault = "Use default";

    private static readonly int[] ProfileBrightnessLevels = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly TileArtwork _artwork;
    private readonly BackdropPresenter _backdrop;
    private readonly ContinuePresenter _continue;
    private readonly ILibraryHost _host;

    // The exe or folder whose properties the open game's menu shows, when it has found one.
    private string? _propertiesOf;

    // The profile open in the menu, and the resolutions and refresh rates its screen offers.
    private GameProfile _profile = GameProfile.None;
    private IReadOnlyList<int> _profileRates = [];
    private IReadOnlyList<Resolution> _profileSizes = [];

    // The optimised settings open in the menu.
    private GameRecommendation? _recommendation;

    public GameActionsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, LibraryOptions options,
        TileArtwork artwork, BackdropPresenter backdrop, ContinuePresenter continuePresenter, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _options = options;
        _artwork = artwork;
        _backdrop = backdrop;
        _continue = continuePresenter;
        _host = host;

        view.InstallItem.Click += (_, _) => Install();
        view.FavouriteItem.Click += (_, _) => ToggleFavourite();
        view.ProfileItem.Click += (_, _) => OpenProfile();
        view.RecommendedItem.Click += (_, _) => OpenRecommended();
        view.MoveItem.Click += (_, _) => ToggleApp();
        view.ArgumentsItem.Click += (_, _) => EditArguments();
        view.FolderItem.Click += (_, _) => OpenFolder();
        view.PropertiesItem.Click += (_, _) => ShowProperties();
        view.ArtworkItem.Click += (_, _) => EditArtworkTitle();
        view.CloseGameItem.Click += (_, _) => CloseGame();
        view.CloseGameItem.LostKeyboardFocus += (_, _) =>
        {
            if (ReferenceEquals(_menu.Armed, view.CloseGameItem))
            {
                _menu.Armed = null;
                if (_menu.Tile is { } tile)
                {
                    RefreshCloseGame(tile);
                }
            }
        };
        view.HideItem.Click += (_, _) => ToggleHidden();

        view.ProfilePowerSetting.Click += (_, _) => StepProfile(view.ProfilePowerSetting, 1);
        view.ProfileResolutionSetting.Click += (_, _) => StepProfile(view.ProfileResolutionSetting, 1);
        view.ProfileRefreshSetting.Click += (_, _) => StepProfile(view.ProfileRefreshSetting, 1);
        view.ProfileBrightnessSetting.Click += (_, _) => StepProfile(view.ProfileBrightnessSetting, 1);
        view.ProfileAppsSetting.Click += (_, _) => StepProfile(view.ProfileAppsSetting, 1);
        view.ProfileAutoTdpSetting.Click += (_, _) => StepProfile(view.ProfileAutoTdpSetting, 1);
        view.ProfileFanSetting.Click += (_, _) => StepProfile(view.ProfileFanSetting, 1);
        view.ProfileSettingsBackItem.Click += (_, _) => menu.Back();
        view.RecommendedApplyItem.Click += (_, _) => ApplyRecommended();
        view.RecommendedBackItem.Click += (_, _) => menu.Back();

        // A game's profile and its optimised settings go back to its quick actions, on the row that opened them.
        menu.SetBack(view.ProfileItems, () => BackToGame(view.ProfileItem));
        menu.SetBack(view.RecommendedItems, () => BackToGame(view.RecommendedItem));
        menu.AddStepper(StepProfile);
    }

    public void Open(GameTile tile)
    {
        if (!_menu.Open(_view.GameItems, tile.Title, Subtitle(tile), tile))
        {
            return;
        }

        _state.FocusedKey = tile.Game.Key;
        Refresh(tile);
    }

    private bool BackToGame(Button opener)
    {
        if (_menu.Tile is not { } tile)
        {
            return false;
        }

        Refresh(tile);
        _menu.Show(_view.GameItems, null, Subtitle(tile), opener);
        return true;
    }

    /// <summary>The store, and how long the game has been played: "Steam   12 h 5 min played, last played today".</summary>
    private string Subtitle(GameTile tile) =>
        _host.IsPaused(tile) ? $"{tile.StoreName}   Paused where you left off"
        : tile.IsApp ? _state.StatsOf(tile).LastPlayed is { } opened
                ? $"App   Last opened {PlayStats.Ago(DateTimeOffset.UtcNow - opened)}"
                : "App"
        : _state.StatsOf(tile).Describe(DateTimeOffset.UtcNow) is { } played
            ? $"{tile.StoreName}   {played}"
            : tile.IsInstalled ? $"{tile.StoreName}   Not played yet"
            : $"{tile.StoreName}   Not installed";

    /// <summary>What each quick action will do for this game, on the rows' detail lines.</summary>
    private void Refresh(GameTile tile)
    {
        var view = _view;
        var game = tile.Game;

        // A game that is not installed can only be installed or hidden.
        var installed = tile.IsInstalled;
        view.InstallItem.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in new[] { view.FavouriteItem, view.ProfileItem, view.ArgumentsItem, view.FolderItem, view.PropertiesItem })
        {
            row.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        }

        view.FavouriteItem.Content = tile.IsFavourite ? "Remove from favourites" : "Add to favourites";

        // Profiles only change things for games.
        view.ProfileItem.Visibility = installed && !tile.IsApp ? Visibility.Visible : Visibility.Collapsed;
        view.ProfileItem.Tag = GameProfiles.For(game) is { IsEmpty: true, CloseApps: null, AutoTdpFps: null, FanCurve: null } ? "Not set" : "Set";

        var recommendation = installed && !tile.IsApp ? GameSettingsDb.Current.For(game) : null;
        view.RecommendedItem.Visibility = recommendation is null ? Visibility.Collapsed : Visibility.Visible;
        view.RecommendedItem.Tag = recommendation?.Target;
        RefreshCloseGame(tile);

        var added = AddedPrograms.IsAdded(game);
        view.MoveItem.Visibility = added ? Visibility.Visible : Visibility.Collapsed;
        view.MoveItem.Content = tile.IsApp ? "Move to Games" : "Move to Apps";
        view.HideItem.Content = added ? "Remove from library" : tile.IsHidden ? "Show in library" : "Hide from library";

        var takesArguments = GameCatalog.SupportsCustomArguments(game);
        view.ArgumentsItem.IsEnabled = takesArguments;
        view.ArgumentsItem.Tag = !takesArguments ? "Not available"
            : GameCatalog.CustomArguments(game) is { Length: > 0 } arguments ? arguments
            : "None";

        var folder = game.InstallDirectory is { } dir && Directory.Exists(dir) ? dir : null;
        view.FolderItem.IsEnabled = folder is not null;
        view.FolderItem.Tag = folder is null ? "Not found" : null;

        // Steam does not record which exe is the game, so its games show the folder's properties.
        var exe = game.ExecutablePath is { } path && File.Exists(path) ? path : null;
        view.PropertiesItem.IsEnabled = (exe ?? folder) is not null;
        view.PropertiesItem.Content = exe is null && folder is not null ? "Install folder properties" : "Executable properties";
        view.PropertiesItem.Tag = (exe ?? folder) is null ? "Not found" : null;
        _propertiesOf = exe ?? folder;

        var canLookUp = Artwork.HasApiKey;
        // Wherever SteamGridDB's answer is on show: behind the library, on an Old School card, and on
        // every tile of an app, whose name is all there is to look it up by.
        view.ArtworkItem.Visibility = _options.Background == BackgroundKind.Artwork || _host.Layout == LibraryLayout.OldSchool || tile.IsApp
            ? Visibility.Visible : Visibility.Collapsed;
        view.ArtworkItem.IsEnabled = canLookUp;
        view.ArtworkItem.Tag = !canLookUp ? "Needs a SteamGridDB key" : Artwork.SearchTitle(game);
    }

    private void Install()
    {
        if (_menu.Tile is { } tile)
        {
            _menu.Close();
            _host.Launch(tile);
        }
    }

    // ----- Favourites, hiding, and the programs the player added -----

    private void ToggleFavourite()
    {
        if (_menu.Tile is not { } tile)
        {
            return;
        }

        var favourite = !tile.IsFavourite;
        _menu.Close();

        if (!GameCatalog.SetFavourite(tile.Game, favourite))
        {
            _notices.Show($"{tile.Title} could not be saved as a favourite.");
            return;
        }

        tile.IsFavourite = favourite;
        _host.ShowTiles();
        _host.RestoreFocus();
        _notices.Show(favourite ? $"{tile.Title} added to favourites." : $"{tile.Title} removed from favourites.");
    }

    /// <summary>
    /// Moves a program the player added between the games and Apps, and goes to the tab it is on now so
    /// it stays in view.
    /// </summary>
    private void ToggleApp()
    {
        if (_menu.Tile is not { } tile)
        {
            return;
        }

        var app = !tile.IsApp;
        _menu.Close();

        if (AddedPrograms.SetApp(tile.Game, app) is not { } moved)
        {
            _notices.Show($"{tile.Title} could not be moved.");
            return;
        }

        var replacement = GameTile.Create(moved);
        replacement.IsFavourite = tile.IsFavourite;
        replacement.IsHidden = tile.IsHidden;
        _state.Replace(tile, replacement);

        _state.FocusedKey = moved.Key;
        _continue.FocusWasHere = false;
        _state.Filter = app ? TileFilter.AppsTab : TileFilter.All;
        _host.ShowTiles();
        _host.RestoreFocus();
        _notices.Show(app ? $"{tile.Title} moved to Apps." : $"{tile.Title} moved to Games.");
    }

    /// <summary>Hides or shows a game, or takes a program the player added off the library altogether.</summary>
    private void ToggleHidden()
    {
        if (_menu.Tile is not { } tile)
        {
            return;
        }

        // The focus moves on to the next game rather than jumping back to the first.
        var leaving = AddedPrograms.IsAdded(tile.Game) || (!tile.IsHidden && !_options.ShowHidden);
        if (leaving && _state.Tiles.IndexOf(tile) is var index and >= 0)
        {
            var next = index + 1 < _state.Tiles.Count ? index + 1 : index - 1;
            _state.FocusedKey = next >= 0 ? _state.Tiles[next].Game.Key : null;
        }

        _menu.Close();

        if (AddedPrograms.IsAdded(tile.Game))
        {
            if (!AddedPrograms.Remove(tile.Game))
            {
                _notices.Show($"{tile.Title} could not be removed.");
                return;
            }

            _state.Remove(tile);
            _host.ShowTiles();
            _host.RestoreFocus();
            _notices.Show($"{tile.Title} removed from the library.");
            return;
        }

        var hidden = !tile.IsHidden;
        if (!GameCatalog.SetHidden(tile.Game, hidden))
        {
            _notices.Show($"{tile.Title} could not be {(hidden ? "hidden" : "shown")}.");
            return;
        }

        tile.IsHidden = hidden;
        _host.ShowTiles();
        _host.RestoreFocus();
        _notices.Show(!hidden ? $"{tile.Title} is back in the library."
            : _options.ShowHidden ? $"{tile.Title} is hidden. It shows faded while Show hidden games is on."
            : $"{tile.Title} is hidden. Turn on Show hidden games to bring it back. It is under Appearance & Layout, Library Elements.");
    }

    // ----- Typed settings: launch arguments and the artwork title -----

    private void EditArguments()
    {
        if (_menu.Tile is { } tile)
        {
            _menu.BeginEdit("Launch arguments", "Added to the end of the command that starts this game.",
                GameCatalog.CustomArguments(tile.Game), text =>
                {
                    var saved = GameCatalog.SaveCustomArguments(tile.Game, text);
                    _menu.Close();

                    _notices.Show(!saved ? $"The launch arguments for {tile.Title} could not be saved."
                        : text.Length == 0 ? $"Launch arguments cleared for {tile.Title}."
                        : $"Launch arguments saved for {tile.Title}.");
                });
        }
    }

    private void EditArtworkTitle()
    {
        if (_menu.Tile is not { } tile)
        {
            return;
        }

        _menu.BeginEdit("Artwork",
            "The name to search SteamGridDB for. Use it when the artwork is wrong or missing. Leave empty to use the game's own name.",
            Artwork.SearchTitle(tile.Game), text =>
            {
                var saved = Artwork.SaveSearchTitle(tile.Game, text);

                // Everything was looked up under the old title too.
                if (saved)
                {
                    _artwork.LookUpAgain(tile);
                }

                // Closing the menu puts focus back on the tile, which loads its background again.
                _backdrop.Forget();
                _menu.Close();

                _notices.Show(!saved ? $"The artwork title for {tile.Title} could not be saved."
                    : text.Length == 0 ? $"Looking up artwork for {tile.Title} by its own title."
                    : $"Looking up artwork for {tile.Title} as \"{text}\".");
            });
    }

    // ----- Its folder and properties -----

    private void OpenFolder()
    {
        if (_menu.Tile?.Game.InstallDirectory is not { } folder)
        {
            return;
        }

        _menu.Close();
        try
        {
            // The trailing separator makes sure a folder opens, never a same-named program beside it.
            Process.Start(new ProcessStartInfo(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar)
            {
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Program.Log($"The install folder could not be opened: {ex.Message}");
            _notices.Show("The install folder could not be opened.");
        }
    }

    private void ShowProperties()
    {
        if (_menu.Tile is null || _propertiesOf is not { } target)
        {
            return;
        }

        _menu.Close();
        if (!Native.ShowProperties(target))
        {
            _notices.Show("The properties window could not be opened.");
        }
    }

    // ----- Close game, while it runs -----

    private void RefreshCloseGame(GameTile tile)
    {
        var running = _host.Session is { StartedAt: not null } session && session.Game.Key == tile.Game.Key;
        _view.CloseGameItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _view.CloseGameItem.Tag = ReferenceEquals(_menu.Armed, _view.CloseGameItem) ? "Press again to close it" : null;
    }

    private void CloseGame()
    {
        if (_menu.Tile is not { } tile || _host.Session is not { StartedAt: not null } session || session.Game.Key != tile.Game.Key)
        {
            return;
        }

        if (!ReferenceEquals(_menu.Armed, _view.CloseGameItem))
        {
            _menu.Armed = _view.CloseGameItem;
            RefreshCloseGame(tile);

            // The warning was under the row's name, and is given now, before the press that counts.
            _notices.Show("Anything not saved in the game is lost.");
            return;
        }

        _menu.Close();
        _ = _host.CloseGameAsync(session);
    }

    // ----- Its profile -----

    private void OpenProfile()
    {
        if (_menu.Tile is not { } tile)
        {
            return;
        }

        _profile = GameProfiles.For(tile.Game);
        _profileRates = RefreshRates.Read()?.Available ?? [];
        _profileSizes = Resolutions.Read()?.Available ?? [];

        RefreshProfile();
        _menu.Show(_view.ProfileItems, null, "Applied when the game starts, undone when it closes");
    }

    private void RefreshProfile()
    {
        var view = _view;
        var powerModes = PowerModes.Available;
        view.ProfilePowerSetting.IsEnabled = powerModes || _profile.PowerMode is not null;
        view.ProfilePowerSetting.Tag = _profile.PowerMode is { } mode ? PowerModes.Name(mode)
            : powerModes ? Unchanged
            : "Not available";

        view.ProfileResolutionSetting.IsEnabled = _profileSizes.Count > 1 || _profile.Resolution is not null;
        view.ProfileResolutionSetting.Tag = _profile.Resolution is { } size ? size.ToString()
            : _profileSizes.Count > 1 ? Unchanged
            : "Not available";

        view.ProfileRefreshSetting.IsEnabled = _profileRates.Count > 1 || _profile.RefreshRate is not null;
        view.ProfileRefreshSetting.Tag = _profile.RefreshRate is { } hertz ? $"{hertz} Hz"
            : _profileRates.Count > 1 ? Unchanged
            : "Not available";

        view.ProfileBrightnessSetting.Tag = _profile.Brightness is { } level ? $"{level}%" : Unchanged;

        view.ProfileAppsSetting.Tag = _profile.CloseApps switch
        {
            true => "Closed",
            false => "Left running",
            null => Unchanged
        };

        view.ProfileAutoTdpSetting.Tag = _profile.AutoTdpFps switch
        {
            null => UseDefault,
            0 => "Off",
            var fps => $"{fps} fps"
        };

        view.ProfileFanSetting.Tag = _profile.FanCurve is { } fans ? AutoTdp.FanName(fans) : UseDefault;
    }

    /// <summary>Left and right on a profile row. Each row's choices start with Don't change.</summary>
    private bool StepProfile(Button row, int step)
    {
        var view = _view;
        GameProfile profile;
        if (ReferenceEquals(row, view.ProfilePowerSetting))
        {
            profile = _profile with { PowerMode = SettingSteps.StepChoice(_profile.PowerMode, Enum.GetValues<PowerMode>(), step) };
        }
        else if (ReferenceEquals(row, view.ProfileResolutionSetting))
        {
            // As with refresh rates, a size saved on another screen stays one of the choices.
            var sizes = _profileSizes.ToList();
            if (_profile.Resolution is { } chosen && !sizes.Contains(chosen))
            {
                sizes.Add(chosen);
            }

            profile = _profile with { Resolution = SettingSteps.StepChoice(_profile.Resolution, sizes, step) };
        }
        else if (ReferenceEquals(row, view.ProfileRefreshSetting))
        {
            // A rate saved while another screen was in use stays one of the choices.
            var rates = _profileRates.ToList();
            if (_profile.RefreshRate is { } chosen && !rates.Contains(chosen))
            {
                rates.Add(chosen);
                rates.Sort();
            }

            profile = _profile with { RefreshRate = SettingSteps.StepChoice(_profile.RefreshRate, rates, step) };
        }
        else if (ReferenceEquals(row, view.ProfileBrightnessSetting))
        {
            profile = _profile with { Brightness = SettingSteps.StepChoice(_profile.Brightness, ProfileBrightnessLevels, step) };
        }
        else if (ReferenceEquals(row, view.ProfileAppsSetting))
        {
            profile = _profile with { CloseApps = SettingSteps.StepChoice(_profile.CloseApps, [true, false], step) };
        }
        else if (ReferenceEquals(row, view.ProfileAutoTdpSetting))
        {
            profile = _profile with { AutoTdpFps = SettingSteps.StepChoice(_profile.AutoTdpFps, [0, .. AutoTdp.FrameRates], step) };
        }
        else if (ReferenceEquals(row, view.ProfileFanSetting))
        {
            string?[] options = [null, .. AutoTdp.FanChoices];
            var index = Math.Max(0, Array.IndexOf(options, _profile.FanCurve));
            profile = _profile with { FanCurve = options[(index + step + options.Length) % options.Length] };
        }
        else
        {
            return false;
        }

        if (_menu.Tile is { } tile && GameProfiles.Save(tile.Game, profile))
        {
            _profile = profile;
        }
        else
        {
            _notices.Show("The game profile could not be saved.");
        }

        RefreshProfile();
        return true;
    }

    // ----- Its optimised settings -----

    private void OpenRecommended()
    {
        if (_menu.Tile is not { } tile || GameSettingsDb.Current.For(tile.Game) is not { } recommendation)
        {
            return;
        }

        var view = _view;
        _recommendation = recommendation;
        view.RecommendedNote.Text = recommendation.Note ?? string.Empty;
        view.RecommendedNote.Visibility = string.IsNullOrWhiteSpace(recommendation.Note) ? Visibility.Collapsed : Visibility.Visible;

        view.RecommendedApplyItem.IsEnabled = recommendation.Device is not null;
        view.RecommendedDevice.Text = recommendation.DescribeDevice();
        RefreshRecommendedApply(tile);

        view.RecommendedList.ItemsSource = recommendation.Settings
            .Select(s => new GameSetting(s.Name, s.Value ?? string.Empty))
            .ToList();

        view.RecommendedSource.Text = recommendation.FromGuide && recommendation.Source is { Name.Length: > 0 } named
            ? $"Source: {named.Name}"
            : "A first try, not from a guide";

        _menu.Show(view.RecommendedItems, null, $"Optimised for {recommendation.Target}");
    }

    /// <summary>The device half for this screen: sizes and rates it does not offer are left out.</summary>
    private static GameProfile ApplyForThisScreen(GameRecommendation recommendation, GameProfile profile) =>
        recommendation.ApplyTo(profile,
            Resolutions.Read()?.Available is { Count: > 0 } sizes ? sizes : null,
            RefreshRates.Read()?.Available is { Count: > 0 } rates ? rates : null);

    /// <summary>Whether the game's profile already has the recommended device settings.</summary>
    private void RefreshRecommendedApply(GameTile tile)
    {
        if (_recommendation is not { } recommendation)
        {
            return;
        }

        var profile = GameProfiles.For(tile.Game);
        _view.RecommendedApplyItem.Tag = recommendation.Device is null ? "Not available"
            : ApplyForThisScreen(recommendation, profile) == profile ? "Applied"
            : null;
    }

    private void ApplyRecommended()
    {
        if (_menu.Tile is not { } tile || _recommendation is not { } recommendation)
        {
            return;
        }

        var profile = ApplyForThisScreen(recommendation, GameProfiles.For(tile.Game));
        if (!GameProfiles.Save(tile.Game, profile))
        {
            _notices.Show("The game profile could not be saved.");
            return;
        }

        Program.Log($"Saved the optimised device settings to {tile.Game.Key}'s profile");
        _notices.Show($"Optimised settings saved to {tile.Game.Title}'s profile.");
        RefreshRecommendedApply(tile);
    }
}
