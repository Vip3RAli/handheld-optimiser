using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The library's own settings menu.</summary>
public partial class LibraryWindow
{
    // The player's choices, from the settings menu here or the main app.
    private BackgroundKind _background = LibrarySettings.Background;
    private ColourStrength _strength = LibrarySettings.Strength;
    private ArtworkBlur _blur = LibrarySettings.Blur;
    private ArtworkPosition _position = LibrarySettings.Position;
    private bool _showStatus = LibrarySettings.ShowStatus;
    private bool _showFilter = LibrarySettings.ShowFilter;
    private bool _quickActions = LibrarySettings.QuickActions;
    private bool _showHidden = LibrarySettings.ShowHidden;

    /// <summary>The library's own settings, from the gear in the top bar or the controller's Menu button.</summary>
    private void OpenSettings()
    {
        _menuTile = null;
        _menu = SettingsItems;
        HideMenuLists();

        MenuTitle.Text = "Settings";
        MenuStore.Text = "Game library";
        RefreshSettings();

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    private void OnOpenBackgroundSettings(object sender, RoutedEventArgs e) => OpenSettingsGroup(BackgroundItems, "Background");

    private void OnOpenDisplaySettings(object sender, RoutedEventArgs e) => OpenSettingsGroup(DisplayItems, "Display");

    /// <summary>One group of settings, in place of the settings list.</summary>
    private void OpenSettingsGroup(StackPanel group, string title)
    {
        SettingsItems.Visibility = Visibility.Collapsed;
        _menu = group;

        MenuTitle.Text = title;
        MenuStore.Text = "Settings";
        ShowMenuItems();
    }

    private void OnSettingsBack(object sender, RoutedEventArgs e) => BackToSettings();

    /// <summary>
    /// From a group of settings, goes back to the settings list with that group's row focused, and from a
    /// game's profile back to its quick actions. False when no group is open, so Back closes the menu instead.
    /// </summary>
    private bool BackToSettings()
    {
        if (ReferenceEquals(_menu, ProfileItems) && _menuTile is { } tile)
        {
            HideMenuLists();
            _menu = GameItems;
            MenuStore.Text = GameSubtitle(tile);
            RefreshGameItems(tile);
            ShowMenuItems(ProfileItem);
            return true;
        }

        // Lists inside a group go back to that group.
        if (ReferenceEquals(_menu, AppsToCloseItems))
        {
            OpenWhilePlaying(AppsToCloseGroup);
            return true;
        }

        if (ReferenceEquals(_menu, RunningAppsItems))
        {
            OpenAppsToClose(AddRunningAppItem);
            return true;
        }

        if (ReferenceEquals(_menu, StartupItems))
        {
            if (ReferenceEquals(_startupFrom, StoreStartupItem))
            {
                OpenWhilePlaying(StoreStartupItem);
            }
            else
            {
                OpenMaintenance(StartupGroup);
            }

            return true;
        }

        var row = ReferenceEquals(_menu, BackgroundItems) ? BackgroundGroup
            : ReferenceEquals(_menu, DisplayItems) ? DisplayGroup
            : ReferenceEquals(_menu, AddItems) ? AddProgramGroup
            : ReferenceEquals(_menu, WhilePlayingItems) ? WhilePlayingGroup
            : ReferenceEquals(_menu, EmulatorItems) ? EmulatorsGroup
            : ReferenceEquals(_menu, MaintenanceItems) ? MaintenanceGroup
            : null;

        if (row is null)
        {
            return false;
        }

        HideMenuLists();
        _menu = SettingsItems;
        _confirming = null;

        MenuTitle.Text = "Settings";
        MenuStore.Text = "Game library";
        ShowMenuItems(row);
        return true;
    }

    private void RefreshSettings()
    {
        UpdateSetting.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSetting.Tag = _update is null ? null : $"Handheld Optimiser {Updates.Display(_update)} is available";

        BackgroundSetting.Tag = _background switch
        {
            BackgroundKind.Artwork => "Game artwork",
            BackgroundKind.Plain => "Plain",
            _ => "Game colours"
        };

        // Artwork mode still shows colours for a game with no artwork, so the strength applies there too.
        StrengthSetting.IsEnabled = _background != BackgroundKind.Plain;
        StrengthSetting.Tag = _strength.ToString();

        BlurSetting.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        BlurSetting.Tag = _blur.ToString();

        PositionSetting.Visibility = BlurSetting.Visibility;
        PositionSetting.Tag = _position.ToString();

        StatusSetting.Tag = OnOff(_showStatus);
        FilterSetting.Tag = OnOff(_showFilter);
        QuickActionsSetting.Tag = OnOff(_quickActions);
        SortSetting.Tag = SortName(_sort) + ". Favourites always come first";

        var hidden = _allTiles.Count(t => t.IsHidden);
        HiddenSetting.Tag = (_showHidden ? "On, faded in the grid" : "Off")
            + (hidden == 0 ? ". No games are hidden" : hidden == 1 ? ". 1 game is hidden" : $". {hidden} games are hidden");

        // Only the end of the key is shown: enough to tell which one it is.
        ArtworkKeySetting.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkKeySetting.Tag = LibrarySettings.ArtworkKey is not { } key ? "Not set. Free to create at steamgriddb.com"
            : key.Length > 4 ? $"Set, ending in {key[^4..]}"
            : "Set";
    }

    private static string OnOff(bool on) => on ? "On" : "Off";

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnToggleStatus(object sender, RoutedEventArgs e)
    {
        _showStatus = !_showStatus;
        LibrarySettings.ShowStatus = _showStatus;
        RefreshSettings();
        _ = UpdateStatusAsync();
    }

    private void OnToggleFilter(object sender, RoutedEventArgs e)
    {
        _showFilter = !_showFilter;
        LibrarySettings.ShowFilter = _showFilter;
        RefreshSettings();

        // Switching it off also drops a store filter that could no longer be changed.
        ApplyFilter();
    }

    private void OnToggleQuickActions(object sender, RoutedEventArgs e)
    {
        _quickActions = !_quickActions;
        LibrarySettings.QuickActions = _quickActions;
        OptionsHint.Visibility = _quickActions ? Visibility.Visible : Visibility.Collapsed;
        RefreshSettings();
    }

    private void OnToggleShowHidden(object sender, RoutedEventArgs e)
    {
        _showHidden = !_showHidden;
        LibrarySettings.ShowHidden = _showHidden;
        RefreshSettings();
        ApplyFilter();
    }

    private void OnCycleBackground(object sender, RoutedEventArgs e) => StepBackground(1);

    private void OnCycleStrength(object sender, RoutedEventArgs e) => StepStrength(1);

    private void OnCycleBlur(object sender, RoutedEventArgs e) => StepBlur(1);

    private void OnCyclePosition(object sender, RoutedEventArgs e) => StepPosition(1);

    /// <summary>
    /// Opens the main app over the library, which stays open behind it. If it is already open it is
    /// brought to the front instead, with no second copy and no administrator prompt.
    /// </summary>
    private void OnOpenMainApp(object sender, RoutedEventArgs e)
    {
        CloseMenu();

        if (MainApp.ShowIfRunning())
        {
            Program.Log("Switched to the main app, which was already open");
            StatusText.Text = "Handheld Optimiser is already open.";
            return;
        }

        if (MainApp.Start() is { } problem)
        {
            Program.Log($"Could not open the main app from settings: {problem}");
            StatusText.Text = $"Handheld Optimiser was not opened: {problem}";
            return;
        }

        Program.Log("Opened the main app from settings");
        StatusText.Text = "Opening Handheld Optimiser...";
    }

    /// <summary>Closes the library. Windows starts it again on the next home button press.</summary>
    private void OnQuit(object sender, RoutedEventArgs e)
    {
        Program.Log("Game library closed from its settings");
        Close();
    }

    private void StepBackground(int step)
    {
        _background = Step(_background, step);
        LibrarySettings.Background = _background;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepStrength(int step)
    {
        _strength = Step(_strength, step);
        LibrarySettings.Strength = _strength;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepPosition(int step)
    {
        _position = Step(_position, step);
        LibrarySettings.Position = _position;
        RefreshSettings();
        ReloadBackdrop();
    }

    private void StepBlur(int step)
    {
        _blur = Step(_blur, step);
        LibrarySettings.Blur = _blur;
        RefreshSettings();
        ReloadBackdrop();
    }

    /// <summary>The next or previous choice of a setting, going round at either end.</summary>
    private static T Step<T>(T value, int step) where T : struct, Enum
    {
        var choices = Enum.GetValues<T>();
        return choices[(Array.IndexOf(choices, value) + step + choices.Length) % choices.Length];
    }

    /// <summary>Left and right on a settings row with several choices step through them, like a slider.</summary>
    private bool StepFocusedSetting(int step)
    {
        if (Keyboard.FocusedElement is not Button row)
        {
            return false;
        }

        if (ReferenceEquals(_menu, QuickItems))
        {
            return StepQuickSetting(row, step);
        }

        if (ReferenceEquals(_menu, ProfileItems))
        {
            return StepProfileSetting(row, step);
        }

        if (ReferenceEquals(_menu, EmulatorItems))
        {
            return StepEmulatorSetting(row, step);
        }

        if (StepToolsSetting(row, step))
        {
            return true;
        }

        if (!ReferenceEquals(_menu, BackgroundItems))
        {
            return false;
        }

        if (ReferenceEquals(row, BackgroundSetting))
        {
            StepBackground(step);
        }
        else if (ReferenceEquals(row, StrengthSetting))
        {
            StepStrength(step);
        }
        else if (ReferenceEquals(row, PositionSetting))
        {
            StepPosition(step);
        }
        else if (ReferenceEquals(row, BlurSetting))
        {
            StepBlur(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void OnEditArtworkKey(object sender, RoutedEventArgs e) =>
        BeginEdit(MenuEditor.ArtworkKey, "SteamGridDB API key",
            "Free at steamgriddb.com, under Preferences then API. Fetches artwork for games outside Steam, whose titles are sent to SteamGridDB. Leave empty to remove the key.",
            LibrarySettings.ArtworkKey);
}
