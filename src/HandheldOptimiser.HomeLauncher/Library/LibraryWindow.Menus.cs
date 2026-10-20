using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The overlay menu and a game's quick actions, including the text box for typed settings.</summary>
public partial class LibraryWindow
{
    private enum MenuEditor
    {
        Arguments,
        ArtworkTitle,
        ArtworkKey,
        SteamKey,
        DealsKey
    }

    // The list showing in the overlay: a game's quick actions, the library's settings or the power menu.
    // Null when closed.
    private StackPanel? _menu;

    // The game the quick actions are for, and which text setting is being typed.
    private GameTile? _menuTile;

    // The exe or folder whose properties the open game's menu shows, when it has found one.
    private string? _propertiesOf;
    private MenuEditor? _editing;

    private void OpenMenu(GameTile tile)
    {
        _menuTile = tile;
        _focusedKey = tile.Game.Key;
        _menu = GameItems;
        HideMenuLists();

        MenuTitle.Text = tile.Title;
        MenuStore.Text = GameSubtitle(tile);
        RefreshGameItems(tile);

        ShowMenuOverlay();
        ShowMenuItems();
    }

    /// <summary>Every list the overlay can show, collapsed, before one of them is opened.</summary>
    private void HideMenuLists()
    {
        foreach (var list in new[]
        {
            GameItems, ProfileItems, RecommendedItems, SettingsItems, LibraryDisplayItems, GamesAppsItems, SleepTweaksItems,
            BackgroundItems, DisplayItems, AddItems, PowerItems, QuickItems,
            WhilePlayingItems, AppsToCloseItems, RunningAppsItems, MaintenanceItems, StartupItems, EmulatorItems,
            TvItems, OwnedItems, DealsItems
        })
        {
            list.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The store, and how long the game has been played: "Steam   12 h 5 min played, last played today".</summary>
    private string GameSubtitle(GameTile tile) =>
        IsPausedGame(tile) ? $"{tile.StoreName}   Paused where you left off"
        : tile.IsApp ? _stats.GetValueOrDefault(tile.Game.Key).LastPlayed is { } opened
                ? $"App   Last opened {PlayStats.Ago(DateTimeOffset.UtcNow - opened)}"
                : "App"
        : _stats.GetValueOrDefault(tile.Game.Key).Describe(DateTimeOffset.UtcNow) is { } played
            ? $"{tile.StoreName}   {played}"
            : tile.IsInstalled ? $"{tile.StoreName}   Not played yet"
            : $"{tile.StoreName}   Not installed";

    /// <summary>What each quick action will do for this game, on the rows' detail lines.</summary>
    private void RefreshGameItems(GameTile tile)
    {
        var game = tile.Game;

        // A game that is not installed can only be installed or hidden.
        var installed = tile.IsInstalled;
        InstallItem.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in new[] { FavouriteItem, ProfileItem, ArgumentsItem, FolderItem, PropertiesItem })
        {
            row.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        }

        FavouriteItem.Content = tile.IsFavourite ? "Remove from favourites" : "Add to favourites";

        // Profiles only change things for games.
        ProfileItem.Visibility = installed && !tile.IsApp ? Visibility.Visible : Visibility.Collapsed;
        ProfileItem.Tag = GameProfiles.For(game) is { IsEmpty: true, CloseApps: null, AutoTdpFps: null, FanCurve: null } ? "Not set" : "Set";

        var recommendation = installed && !tile.IsApp ? GameSettingsDb.Current.For(game) : null;
        RecommendedItem.Visibility = recommendation is null ? Visibility.Collapsed : Visibility.Visible;
        RecommendedItem.Tag = recommendation?.Target;
        RefreshCloseGameItem(tile);

        var added = AddedPrograms.IsAdded(game);
        MoveItem.Visibility = added ? Visibility.Visible : Visibility.Collapsed;
        MoveItem.Content = tile.IsApp ? "Move to Games" : "Move to Apps";
        HideItem.Content = added ? "Remove from library" : tile.IsHidden ? "Show in library" : "Hide from library";

        var takesArguments = GameCatalog.SupportsCustomArguments(game);
        ArgumentsItem.IsEnabled = takesArguments;
        ArgumentsItem.Tag = !takesArguments ? "Not available"
            : GameCatalog.CustomArguments(game) is { Length: > 0 } arguments ? arguments
            : "None";

        var folder = game.InstallDirectory is { } dir && Directory.Exists(dir) ? dir : null;
        FolderItem.IsEnabled = folder is not null;
        FolderItem.Tag = folder is null ? "Not found" : null;

        // Steam does not record which exe is the game, so its games show the folder's properties.
        var exe = game.ExecutablePath is { } path && File.Exists(path) ? path : null;
        PropertiesItem.IsEnabled = (exe ?? folder) is not null;
        PropertiesItem.Content = exe is null && folder is not null ? "Install folder properties" : "Executable properties";
        PropertiesItem.Tag = (exe ?? folder) is null ? "Not found" : null;
        _propertiesOf = exe ?? folder;

        var canLookUp = Artwork.HasApiKey;
        // Wherever SteamGridDB's answer is on show: behind the library, on an Old School card, and on
        // every tile of an app, whose name is all there is to look it up by.
        ArtworkItem.Visibility = _background == BackgroundKind.Artwork || _layout == LibraryLayout.OldSchool || tile.IsApp
            ? Visibility.Visible : Visibility.Collapsed;
        ArtworkItem.IsEnabled = canLookUp;
        ArtworkItem.Tag = !canLookUp ? "Needs a SteamGridDB key" : Artwork.SearchTitle(game);
    }

    /// <summary>The open list of rows, which is also where cancelling the text box goes back to.</summary>
    private void ShowMenuItems(Button? focus = null)
    {
        if (_menu is null)
        {
            return;
        }

        _editing = null;
        TextEditor.Visibility = Visibility.Collapsed;
        _menu.Visibility = Visibility.Visible;
        ShowMenuHints(_menu);

        // From the top, with its heading: the last list may have been left scrolled, and the row that
        // takes the focus is brought back into view if it is further down.
        MenuScroller.ScrollToVerticalOffset(0);

        // Wait for the rows to be laid out before one can take focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => (focus ?? _menu?.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled && b.IsVisible))?.Focus());
    }

    private void CloseMenu()
    {
        if (_menu is null)
        {
            return;
        }

        _menu = null;
        _menuTile = null;
        _editing = null;
        _confirming = null;
        HideMenuOverlay();
        PlaceNotice();
        RestoreFocus();
    }

    private void MoveMenuFocus(FocusNavigationDirection direction)
    {
        if (direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
        {
            StepFocusedSetting(direction == FocusNavigationDirection.Right ? 1 : -1);
            return;
        }

        var step = direction switch
        {
            FocusNavigationDirection.Up => -1,
            FocusNavigationDirection.Down => 1,
            _ => 0
        };

        var items = _menu?.Children.OfType<Button>().Where(b => b.IsEnabled && b.IsVisible).ToList() ?? [];
        if (step == 0 || items.Count == 0)
        {
            return;
        }

        var index = Keyboard.FocusedElement is Button focused ? items.IndexOf(focused) : -1;
        items[Math.Clamp(index + step, 0, items.Count - 1)].Focus();
    }

    private void OnMenuBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MenuOverlay))
        {
            CloseMenu();
        }
    }

    private void OnEditArguments(object sender, RoutedEventArgs e)
    {
        if (_menuTile is { } tile)
        {
            BeginEdit(MenuEditor.Arguments, "Launch arguments",
                "Added to the end of the command that starts this game.",
                GameCatalog.CustomArguments(tile.Game));
        }
    }

    private void OnEditArtworkTitle(object sender, RoutedEventArgs e)
    {
        if (_menuTile is { } tile)
        {
            BeginEdit(MenuEditor.ArtworkTitle, "Artwork",
                "The name to search SteamGridDB for. Use it when the artwork is wrong or missing. Leave empty to use the game's own name.",
                Artwork.SearchTitle(tile.Game));
        }
    }

    private void BeginEdit(MenuEditor editor, string title, string hint, string? text)
    {
        if (_menu is null)
        {
            return;
        }

        _editing = editor;
        _menu.Visibility = Visibility.Collapsed;
        TextEditor.Visibility = Visibility.Visible;

        EditorTitle.Text = title;
        EditorHint.Text = hint;
        EditorBox.Text = text ?? string.Empty;
        EditorBox.CaretIndex = EditorBox.Text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EditorBox.Focus());
    }

    private void OnSaveEdit(object sender, RoutedEventArgs e) => SaveEdit();

    private void OnCancelEdit(object sender, RoutedEventArgs e) => ShowMenuItems();

    private void SaveEdit()
    {
        if (_editing is not { } editor)
        {
            return;
        }

        var text = EditorBox.Text.Trim();
        if (editor == MenuEditor.SteamKey)
        {
            // Back to the list, then Steam is asked for the games with the new key.
            LibrarySettings.SteamKey = text;
            _ownedError = null;
            RefreshSettings();
            ShowMenuItems();
            if (text.Length > 0)
            {
                SteamKeySetting.Tag = "Asking...";
                _ = RefreshOwnedAsync(force: true);
            }

            return;
        }

        if (editor == MenuEditor.DealsKey)
        {
            SaveDealsKey(text);
            return;
        }

        if (editor == MenuEditor.ArtworkKey)
        {
            // Back to the settings list, with the focused game's artwork looked up under the new key,
            // and the covers that the old key (or having none) could not get.
            LibrarySettings.ArtworkKey = text;
            _coversAsked.Clear();
            RefreshSettings();
            ShowMenuItems();
            ReloadBackdrop();
            _ = FetchCoversAsync();
            return;
        }

        if (_menuTile is not { } tile)
        {
            return;
        }

        if (editor == MenuEditor.Arguments)
        {
            var saved = GameCatalog.SaveCustomArguments(tile.Game, text);
            CloseMenu();

            Notice = !saved ? $"The launch arguments for {tile.Title} could not be saved."
                : text.Length == 0 ? $"Launch arguments cleared for {tile.Title}."
                : $"Launch arguments saved for {tile.Title}.";
        }
        else
        {
            var saved = Artwork.SaveSearchTitle(tile.Game, text);

            // The cover was looked up under the old title too, so it goes back to the icon until the
            // new title's cover arrives.
            if (saved)
            {
                tile.ClearFetchedCover();
                _coversAsked.Remove(tile.Game.Key);
                _ = FetchCoversAsync();

                // Its square icon in the row was looked up under the old title as well.
                tile.ShowSquare(null);
                _squaresAsked.Remove(tile.Game.Key);
                LoadSquares();

                // And the picture on its Old School card.
                tile.ShowWide(null);
                _noCardArt.Remove(tile.Game.Key);
                LoadCardArt();
            }

            // Closing the menu puts focus back on the tile, which loads its background again.
            _backdropKey = null;
            CloseMenu();

            Notice = !saved ? $"The artwork title for {tile.Title} could not be saved."
                : text.Length == 0 ? $"Looking up artwork for {tile.Title} by its own title."
                : $"Looking up artwork for {tile.Title} as \"{text}\".";
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_menuTile?.Game.InstallDirectory is not { } folder)
        {
            return;
        }

        CloseMenu();
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
            Notice = "The install folder could not be opened.";
        }
    }

    private void OnShowProperties(object sender, RoutedEventArgs e)
    {
        if (_menuTile is null || _propertiesOf is not { } target)
        {
            return;
        }

        CloseMenu();
        if (!Native.ShowProperties(target))
        {
            Notice = "The properties window could not be opened.";
        }
    }
}
