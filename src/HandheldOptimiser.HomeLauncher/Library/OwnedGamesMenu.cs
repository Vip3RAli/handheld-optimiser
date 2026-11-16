using System.IO;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Games not installed: whether the library lists the games the player owns on Steam, Epic or GOG but has
/// not installed, the Steam Web API key that lists the Steam ones, and asking Steam for them again.
/// </summary>
internal sealed class OwnedGamesMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ILibraryHost _host;

    private bool _refreshing;
    private string? _error;

    public OwnedGamesMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _host = host;

        view.OwnedGroup.Click += (_, _) => Open();
        view.NotInstalledSetting.Click += (_, _) => ToggleNotInstalled();
        view.SteamKeySetting.Click += (_, _) => EditSteamKey();
        view.OwnedRefreshItem.Click += (_, _) =>
        {
            _notices.Show("Looking for your games...");
            _ = RefreshNowAsync();
        };
        view.OwnedSettingsBackItem.Click += (_, _) => menu.Back();
        view.InstallFolderSetting.Click += (_, _) => EditInstallFolder();
        view.DownloadWhilePlayingSetting.Click += (_, _) =>
        {
            LibrarySettings.DownloadWhilePlaying = !LibrarySettings.DownloadWhilePlaying;
            Refresh();
        };
    }

    private void EditInstallFolder() =>
        _menu.BeginEdit("Install folder",
            @"A folder for the games the library installs itself, such as C:\Games or D:\Games. Games already installed stay where they are.",
            LibrarySettings.InstallFolder, text =>
            {
                var folder = text.Trim().TrimEnd('\\');
                if (folder.Length > 0 && !IsUsableFolder(folder))
                {
                    _notices.Show("That is not a folder the library can use. Give a full path, such as D:\\Games.");
                }
                else
                {
                    LibrarySettings.InstallFolder = folder;
                }

                Refresh();
                _menu.ShowItems(_view.InstallFolderSetting);
            });

    /// <summary>A full path on a drive that is there, and not a drive's top folder alone.</summary>
    internal static bool IsUsableFolder(string folder)
    {
        try
        {
            return Path.IsPathFullyQualified(folder) && Path.GetPathRoot(folder) is { } root
                && folder.Length > root.TrimEnd('\\').Length + 1 && Directory.Exists(root);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool Showing => ReferenceEquals(_menu.List, _view.OwnedItems);

    /// <summary>
    /// Asks Steam for the games the player owns, when they have given a key and the last answer is old,
    /// and looks at the library again if the list changed.
    /// </summary>
    public async Task RefreshAsync(bool force)
    {
        var steam = LibrarySettings.SteamKey is not null || StoreSessions.SignedIn(GameStore.Steam);
        var gog = StoreSessions.Get(GameStore.Gog) is not null;
        var epic = StoreSessions.Get(GameStore.Epic) is not null;
        if (_refreshing || !LibrarySettings.ShowNotInstalled || (!steam && !gog && !epic))
        {
            return;
        }

        _refreshing = true;
        try
        {
            var (changed, error) = steam ? await OwnedGames.RefreshSteamAsync(force) : (false, null);

            if (gog)
            {
                var (gogChanged, gogError) = await OwnedGames.RefreshGogAsync(force);
                changed |= gogChanged;
                error ??= gogError;
            }

            if (epic)
            {
                var (epicChanged, epicError) = await OwnedGames.RefreshEpicAsync(force);
                changed |= epicChanged;
                error ??= epicError;
            }

            _error = error;
            if (error is not null && Showing)
            {
                _notices.Show(error);
            }

            if (changed)
            {
                await _host.ScanAsync();
            }
        }
        finally
        {
            _refreshing = false;
        }

        if (Showing)
        {
            Refresh();
        }
    }

    private void Open()
    {
        _menu.Show(_view.OwnedItems, "Ready to Install", SettingsMenu.GamesApps);
        Refresh();
        _notices.Show(_error ?? string.Empty);
    }

    private void ToggleNotInstalled()
    {
        LibrarySettings.ShowNotInstalled = !LibrarySettings.ShowNotInstalled;
        Refresh();
        _ = _host.ScanAsync();
        _ = RefreshAsync(force: false);
    }

    private void EditSteamKey() =>
        _menu.BeginEdit("Steam Web API key",
            "Free at steamcommunity.com/dev/apikey. Used to list the Steam games you own, and only sent to Steam. Leave empty to remove it.",
            LibrarySettings.SteamKey, text =>
            {
                // Back to the list, then Steam is asked for the games with the new key.
                LibrarySettings.SteamKey = text;
                _error = null;
                Refresh();
                _menu.ShowItems();
                if (text.Length > 0)
                {
                    _view.SteamKeySetting.Tag = "Asking...";
                    _ = RefreshAsync(force: true);
                }
            });

    private async Task RefreshNowAsync()
    {
        await RefreshAsync(force: true);
        await _host.ScanAsync();
        var count = _state.AllTiles.Count(t => !t.IsInstalled);
        _notices.Show(_error ?? (count == 1 ? "1 game ready to install." : $"{count} games ready to install."));
    }

    private void Refresh()
    {
        var on = LibrarySettings.ShowNotInstalled;
        _view.NotInstalledSetting.Tag = SettingSteps.OnOff(on);

        _view.SteamKeySetting.IsEnabled = on;
        // Signed in to Steam inside the library, the key is not used for the list.
        _view.SteamKeySetting.Tag = StoreSessions.SignedIn(GameStore.Steam) ? "Not needed"
            : LibrarySettings.SteamKey is null ? "Not set" : _error is null ? "Set" : "Not working";

        _view.OwnedRefreshItem.IsEnabled = on;

        _view.InstallFolderSetting.Tag = LibrarySettings.InstallFolder;
        _view.DownloadWhilePlayingSetting.Tag = SettingSteps.OnOff(LibrarySettings.DownloadWhilePlaying);
    }
}
