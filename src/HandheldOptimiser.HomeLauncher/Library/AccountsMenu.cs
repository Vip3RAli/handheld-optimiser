using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Store accounts and achievement syncing under Catalog &amp; Content: shows each store's local sign-in state,
/// opens its native sign-in window on press, and syncs achievements across all installed games.
/// </summary>
internal sealed class AccountsMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ContinuePresenter _continue;

    private bool _syncing;

    public AccountsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, ContinuePresenter continuePresenter)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _continue = continuePresenter;

        view.AccountsGroup.Click += (_, _) => Open();
        view.AccountsSteamKeyItem.Click += (_, _) => EditSteamKey();
        view.AccountsSyncAchievementsItem.Click += (_, _) => SyncAll();
        view.AccountsBackItem.Click += (_, _) => menu.Back();
    }

    private void Open(Button? focus = null)
    {
        RefreshRows();
        _menu.Show(_view.AccountsItems, "Store Accounts & Achievements", SettingsMenu.GamesApps, focus);
    }

    private void RefreshRows()
    {
        _menu.ClearRows(_view.AccountsItems);
        var statuses = StoreAccounts.ReadAll();

        foreach (var status in statuses)
        {
            var store = status.Store;
            _menu.AddRow(_view.AccountsItems, _view.AccountsSteamKeyItem, status.StoreName, status.Describe(), () => SignIn(store));
        }

        _view.AccountsSteamKeyItem.Tag = LibrarySettings.SteamKey is null ? "Not set" : "Set";
        _view.AccountsSyncAchievementsItem.Tag = _syncing ? "Syncing..." : null;
    }

    private void SignIn(GameStore store)
    {
        _menu.Close();
        if (StoreAccounts.SignIn(store) is { } problem)
        {
            _notices.Show(problem);
        }
        else
        {
            _notices.Show($"Opening {Game.NameOf(store)} sign-in...");
        }
    }

    private void EditSteamKey() =>
        _menu.BeginEdit("Steam Web API key",
            "Get one at steamcommunity.com/dev/apikey. Used to sync Steam achievements and list owned games. Leave empty to remove it.",
            LibrarySettings.SteamKey, text =>
            {
                LibrarySettings.SteamKey = text;
                RefreshRows();
                _menu.ShowItems(_view.AccountsSteamKeyItem);
            });

    private async void SyncAll()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        _view.AccountsSyncAchievementsItem.Tag = "Syncing...";
        try
        {
            var games = _state.AllTiles.Select(t => t.Game).ToList();
            var (withAchievements, unlocked) = await Achievements.SyncAllAsync(games);
            _continue.ForgetInfo();
            _continue.Update();

            _notices.Show(withAchievements == 0
                ? "No achievements found yet. Sign in to Steam or GOG Galaxy, or set a Steam Web API key."
                : $"Synced {unlocked} unlocked achievements across {withAchievements} {(withAchievements == 1 ? "game" : "games")}.");
        }
        finally
        {
            _syncing = false;
            if (ReferenceEquals(_menu.List, _view.AccountsItems))
            {
                _view.AccountsSyncAchievementsItem.Tag = null;
            }
        }
    }
}
