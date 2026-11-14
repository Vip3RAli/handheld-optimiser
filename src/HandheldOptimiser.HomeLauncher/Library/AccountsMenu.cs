using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Store accounts and achievement syncing under Catalog &amp; Content. Steam is signed in to inside the
/// library (<see cref="SignInPresenter"/>), which gives the library the account's games and achievements;
/// pressed once signed in, its row syncs them, and a row below signs out. The other stores show what their
/// own apps say on this device and open their apps' sign-in, until their own stages of the work.
/// </summary>
internal sealed class AccountsMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ContinuePresenter _continue;
    private readonly SignInPresenter _signIn;
    private readonly OwnedGamesMenu _owned;

    private bool _syncing;
    private Button? _signOutRow;

    public AccountsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, ContinuePresenter continuePresenter,
        SignInPresenter signIn, OwnedGamesMenu owned)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _continue = continuePresenter;
        _signIn = signIn;
        _owned = owned;

        view.AccountsGroup.Click += (_, _) => Open();
        view.AccountsSteamKeyItem.Click += (_, _) => EditSteamKey();
        view.AccountsSyncAchievementsItem.Click += (_, _) => SyncAll();
        view.AccountsBackItem.Click += (_, _) => menu.Back();
    }

    private void Open(Button? focus = null)
    {
        _menu.Armed = null;
        RefreshRows();
        _menu.Show(_view.AccountsItems, "Store Accounts & Achievements", SettingsMenu.GamesApps, focus);
    }

    private void RefreshRows()
    {
        _menu.ClearRows(_view.AccountsItems);
        var before = _view.AccountsSteamKeyItem;

        // Steam: the library's own sign-in.
        var steam = StoreSessions.Get(GameStore.Steam);
        var steamRow = _menu.AddRow(_view.AccountsItems, before, "Steam", steam?.Describe() ?? "Not signed in", PressSteam);
        MenuRow.SetAbout(steamRow, steam is { NeedsSignIn: false }
            ? "Signed in inside the library: your Steam games are listed and their achievements synced. Press to sync now. Steam itself still needs to be signed in to install and play."
            : "Sign in with the Steam app on your phone, or on Steam's own page, to list your Steam games and sync their achievements without a Web API key.");

        _signOutRow = null;
        if (steam is not null)
        {
            _signOutRow = _menu.AddRow(_view.AccountsItems, before, "Sign out of Steam", null, SignOutSteam);
            MenuRow.SetAbout(_signOutRow, "Forgets the library's Steam sign-in on this device. The Steam app stays signed in.");
        }

        // The other stores, as their apps on this device have them, until they are signed in to here too.
        foreach (var status in StoreAccounts.ReadAll().Where(s => s.Store != GameStore.Steam))
        {
            var store = status.Store;
            _menu.AddRow(_view.AccountsItems, before, status.StoreName, status.Describe(), () => SignInWithApp(store));
        }

        _view.AccountsSteamKeyItem.Tag = StoreSessions.SignedIn(GameStore.Steam) ? "Not needed"
            : LibrarySettings.SteamKey is null ? "Not set" : "Set";
        _view.AccountsSyncAchievementsItem.Tag = _syncing ? "Syncing..." : null;
    }

    private void PressSteam()
    {
        if (StoreSessions.SignedIn(GameStore.Steam))
        {
            _ = SyncSteamAsync();
            return;
        }

        // Not signed in, or Steam wants it done again.
        _menu.Close();
        _signIn.OpenSteam();
    }

    private void SignOutSteam()
    {
        // Pressed twice, as Restart is: once arms it, the second press acts.
        if (!ReferenceEquals(_menu.Armed, _signOutRow))
        {
            _menu.Armed = _signOutRow;
            if (_signOutRow is { } row)
            {
                row.Content = "Press again to sign out";
            }

            return;
        }

        _menu.Armed = null;
        StoreSessions.Delete(GameStore.Steam);
        _ = _signIn.ForgetCookiesAsync(GameStore.Steam);
        Program.Log("Signed out of Steam inside the library");
        _notices.Show("Signed out of Steam in the library. The Steam app is still signed in.");
        RefreshRows();
        _menu.ShowItems();
    }

    private async Task SyncSteamAsync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        _notices.Show("Syncing your Steam games and achievements...");
        try
        {
            await _owned.RefreshAsync(force: true);
            var games = _state.AllTiles.Select(t => t.Game).Where(g => g.Store == GameStore.Steam).ToList();
            var (withAchievements, unlocked) = await Achievements.SyncAllAsync(games);
            _continue.ForgetInfo();
            _continue.Update();

            _notices.Show(!StoreSessions.SignedIn(GameStore.Steam)
                ? "Steam wants you to sign in again. Press Steam to do it."
                : withAchievements == 0
                    ? "Steam games synced. None of the installed ones have achievements yet."
                    : $"Steam synced: {unlocked} achievements unlocked across {withAchievements} {(withAchievements == 1 ? "game" : "games")}.");
        }
        finally
        {
            _syncing = false;
            if (ReferenceEquals(_menu.List, _view.AccountsItems))
            {
                RefreshRows();
            }
        }
    }

    private void SignInWithApp(GameStore store)
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
            "Not needed once signed in to Steam above. Otherwise get one at steamcommunity.com/dev/apikey to sync Steam achievements and list owned games. Leave empty to remove it.",
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
                ? "No achievements found yet. Sign in to Steam above, or to GOG Galaxy."
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
