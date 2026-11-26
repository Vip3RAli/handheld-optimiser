using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Store accounts and achievement syncing under Catalog &amp; Content. Steam, GOG, Epic and Xbox are signed in to inside
/// the library (<see cref="SignInPresenter"/>), which gives the library the account's games and achievements;
/// pressed once signed in, a store's row syncs them, and a row below it signs out. The other stores show what
/// their own apps say on this device and open their apps' sign-in, until their own stages of the work.
/// </summary>
internal sealed class AccountsMenu
{
    /// <summary>The stores signed in to inside the library, in the order their rows are shown.</summary>
    private static readonly GameStore[] SignedInHere = [GameStore.Steam, GameStore.Gog, GameStore.Epic, GameStore.Xbox];

    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ContinuePresenter _continue;
    private readonly SignInPresenter _signIn;
    private readonly OwnedGamesMenu _owned;

    private bool _syncing;

    /// <summary>Told when a store is signed out of, so what shows the account elsewhere can follow.</summary>
    public event Action? SignedOut;

    // Each store's Sign out row, which a first press arms.
    private readonly Dictionary<GameStore, Button> _signOutRows = [];

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
        _menu.Show(_view.AccountsItems, "Store Accounts", SettingsMenu.GamesApps, focus);
    }

    private void RefreshRows()
    {
        _menu.ClearRows(_view.AccountsItems);
        _signOutRows.Clear();
        var before = _view.AccountsSteamKeyItem;

        // The library's own sign-ins.
        foreach (var store in SignedInHere)
        {
            var session = StoreSessions.Get(store);
            var row = _menu.AddRow(_view.AccountsItems, before, Game.NameOf(store), session?.Describe() ?? "Not signed in", () => Press(store));
            MenuRow.SetAbout(row, About(store, session is { NeedsSignIn: false }));

            if (session is not null)
            {
                var name = Game.NameOf(store);
                var label = $"Sign out of {name}";
                Button? signOut = null;
                signOut = _menu.AddRow(_view.AccountsItems, before, label, null, () => SignOut(store));
                signOut.LostKeyboardFocus += (_, _) =>
                {
                    if (ReferenceEquals(_menu.Armed, signOut))
                    {
                        _menu.Armed = null;
                        signOut.Content = label;
                    }
                };
                MenuRow.SetAbout(signOut, store == GameStore.Steam
                    ? "Forgets the library's Steam sign-in on this device. The Steam app stays signed in."
                    : $"Forgets the library's {name} sign-in on this device.");
                _signOutRows[store] = signOut;
            }
        }

        // The other stores, as their apps on this device have them, until they are signed in to here too.
        foreach (var status in StoreAccounts.ReadAll().Where(s => !SignedInHere.Contains(s.Store)))
        {
            var store = status.Store;
            _menu.AddRow(_view.AccountsItems, before, status.StoreName, status.Describe(), () => SignInWithApp(store));
        }

        // The Steam key has one row now, under API Keys. This one stays in the list, unseen, as the place
        // the stores' rows are added above.
        _view.AccountsSteamKeyItem.Visibility = Visibility.Collapsed;
        _view.AccountsSyncAchievementsItem.Tag = _syncing ? "Syncing..." : null;
    }

    private static string About(GameStore store, bool signedIn) => (store, signedIn) switch
    {
        (GameStore.Steam, true) => "Signed in inside the library: your Steam games are listed and their achievements synced. Press to sync now. Steam itself still needs to be signed in to install and play.",
        (GameStore.Steam, false) => "Sign in with the Steam app on your phone, or on Steam's own page, to list your Steam games and sync their achievements without a Web API key.",
        // Xbox Live does not say which games an account owns, so the row says what is listed instead.
        (GameStore.Xbox, true) => "Signed in inside the library: the Xbox games for PC you have played before are listed, and their achievements synced. Press to sync now. The Microsoft Store still installs them.",
        (GameStore.Xbox, false) => "Sign in with your Microsoft account to list the Xbox games for PC you have played before and sync their achievements. With the account Windows uses, it needs no password.",

        // Epic has no achievements service the library can ask, so its row promises none.
        (GameStore.Epic, true) => "Signed in inside the library: your Epic games are listed. Press to list them again now. The Epic Games Launcher still installs and starts them.",
        (GameStore.Epic, false) => "Sign in on Epic's own page to list your Epic games, without opening the Epic Games Launcher first.",
        (_, true) => $"Signed in inside the library: your {Game.NameOf(store)} games are listed and their achievements synced. Press to sync now.",
        _ => $"Sign in on {Game.NameOf(store)}'s own page to list your games and sync their achievements, without {Game.NameOf(store)}'s app.",
    };

    private void Press(GameStore store)
    {
        if (StoreSessions.SignedIn(store))
        {
            _ = SyncAsync(store);
            return;
        }

        // Not signed in, or the store wants it done again.
        _menu.Close();
        _signIn.ReturnToMenu = () => Open();
        if (store == GameStore.Gog)
        {
            _signIn.OpenGog();
        }
        else if (store == GameStore.Epic)
        {
            _signIn.OpenEpic();
        }
        else if (store == GameStore.Xbox)
        {
            _signIn.OpenXbox();
        }
        else
        {
            _signIn.OpenSteam();
        }
    }

    private void SignOut(GameStore store)
    {
        // Pressed twice, as Restart is: once arms it, the second press acts.
        var row = _signOutRows.GetValueOrDefault(store);
        if (!ReferenceEquals(_menu.Armed, row))
        {
            _menu.Armed = row;
            if (row is not null)
            {
                row.Content = "Press again to sign out";
            }

            return;
        }

        _menu.Armed = null;
        StoreSessions.Delete(store);
        _ = _signIn.ForgetCookiesAsync(store);
        var name = Game.NameOf(store);
        Program.Log($"Signed out of {name} inside the library");
        _notices.Show(store == GameStore.Steam
            ? "Signed out of Steam in the library. The Steam app is still signed in."
            : $"Signed out of {name} in the library.");
        RefreshRows();
        _menu.ShowItems();
        SignedOut?.Invoke();
    }

    private async Task SyncAsync(GameStore store)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        var name = Game.NameOf(store);
        _notices.Show(store == GameStore.Epic ? "Listing your Epic games..." : $"Syncing your {name} games and achievements...");
        try
        {
            await _owned.RefreshAsync(force: true);
            var games = _state.AllTiles.Select(t => t.Game).Where(g => g.Store == store).ToList();
            var (withAchievements, unlocked) = await Achievements.SyncAllAsync(games);
            _continue.ForgetInfo();
            _continue.Update();

            _notices.Show(!StoreSessions.SignedIn(store)
                ? $"{name} wants you to sign in again. Press {name} to do it."
                : store == GameStore.Epic && withAchievements == 0
                    ? $"Epic games listed: {_state.AllTiles.Count(t => t.Game.Store == GameStore.Epic)}."
                : withAchievements == 0
                    ? $"{name} games synced. None of the installed ones have achievements yet."
                    : $"{name} synced: {unlocked} achievements unlocked across {withAchievements} {(withAchievements == 1 ? "game" : "games")}.");
        }
        finally
        {
            _syncing = false;
            if (ReferenceEquals(_menu.List, _view.AccountsItems))
            {
                var focusedLabel = System.Windows.Input.Keyboard.FocusedElement is Button focused
                    && _view.AccountsItems.Children.Contains(focused)
                    ? focused.Content as string
                    : null;
                RefreshRows();
                var target = focusedLabel is not null
                    ? _view.AccountsItems.Children.OfType<Button>().FirstOrDefault(b => b.IsVisible && b.Content as string == focusedLabel)
                    : null;
                _menu.ShowItems(target);
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

    private async void SyncAll() => await SyncEveryGameAsync(quiet: false);

    /// <summary>
    /// Syncs every game's achievements once a day by itself, as the library comes to the front after a scan.
    /// Quietly: the player did not ask, so there is no notice, only the row saying it is under way.
    /// </summary>
    public Task SyncDailyAsync()
    {
        // Nothing to ask without a store signed in or the Steam key; the games' own files are read as they open.
        var any = AccountsMenu.SignedInHere.Any(StoreSessions.SignedIn) || LibrarySettings.SteamKey is not null;
        return any && Achievements.SyncAllDue(LibrarySettings.AchievementsSyncedAt, DateTimeOffset.Now)
            ? SyncEveryGameAsync(quiet: true)
            : Task.CompletedTask;
    }

    private async Task SyncEveryGameAsync(bool quiet)
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
            LibrarySettings.AchievementsSyncedAt = DateTimeOffset.Now;
            _continue.ForgetInfo();
            _continue.Update();
            Program.Log($"Synced achievements for every game{(quiet ? " (daily)" : "")}: {unlocked} unlocked across {withAchievements}");

            if (!quiet)
            {
                _notices.Show(withAchievements == 0
                    ? "No achievements found yet. Sign in to a store under Store Accounts."
                    : $"Synced {unlocked} unlocked achievements across {withAchievements} {(withAchievements == 1 ? "game" : "games")}.");
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or TaskCanceledException)
        {
            // Tried again on the next look after the scan, since the time is only kept when it worked.
            Program.Log($"Could not sync every game's achievements: {ex.Message}");
        }
        finally
        {
            _syncing = false;

            // The row is in Games & Stores now, outside this menu's own list.
            _view.AccountsSyncAchievementsItem.Tag = null;
        }
    }
}
