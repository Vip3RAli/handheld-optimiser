using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The library's own settings menu.</summary>
public partial class LibraryWindow
{
    // The player's choices, from the settings menu.
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

        ShowMenuOverlay();
        ShowMenuItems();
    }

    // The categories the settings list opens, each a short list of groups.
    private const string LibraryDisplay = "Library display";
    private const string GamesApps = "Games and apps";
    private const string SleepTweaks = "Sleep and clean-up";

    private void OnOpenLibraryDisplay(object sender, RoutedEventArgs e) => OpenSettingsCategory(LibraryDisplayItems);

    private void OnOpenGamesApps(object sender, RoutedEventArgs e) => OpenSettingsCategory(GamesAppsItems);

    private void OnOpenSleepTweaks(object sender, RoutedEventArgs e) => OpenSettingsCategory(SleepTweaksItems);

    /// <summary>One category's groups, in place of the settings list.</summary>
    private void OpenSettingsCategory(StackPanel category, Button? focus = null)
    {
        var title = ReferenceEquals(category, LibraryDisplayItems) ? LibraryDisplay
            : ReferenceEquals(category, GamesAppsItems) ? GamesApps
            : SleepTweaks;

        OpenSettingsGroup(category, title, "Settings", focus);
    }

    private void OnOpenBackgroundSettings(object sender, RoutedEventArgs e) =>
        OpenSettingsGroup(BackgroundItems, "Art and background", LibraryDisplay);

    private void OnOpenDisplaySettings(object sender, RoutedEventArgs e) =>
        OpenSettingsGroup(DisplayItems, "What it shows", LibraryDisplay);

    /// <summary>One group of settings, in place of the list it was opened from, which is named under its title.</summary>
    private void OpenSettingsGroup(StackPanel group, string title, string from, Button? focus = null)
    {
        HideMenuLists();
        _menu = group;

        MenuTitle.Text = title;
        MenuStore.Text = from;
        ShowMenuItems(focus);
    }

    private void OnSettingsBack(object sender, RoutedEventArgs e)
    {
        // Nowhere to go back to: a list opened from its own Old School card, which closes.
        if (!BackToSettings())
        {
            CloseMenu();
        }
    }

    /// <summary>
    /// From a group of settings, goes back to its category with that group's row focused, from a category
    /// back to the settings list, and from a game's profile back to its quick actions. False when none of
    /// those is open, so Back closes the menu instead.
    /// </summary>
    private bool BackToSettings()
    {
        if ((ReferenceEquals(_menu, ProfileItems) || ReferenceEquals(_menu, RecommendedItems)) && _menuTile is { } tile)
        {
            var opener = ReferenceEquals(_menu, ProfileItems) ? ProfileItem : RecommendedItem;
            HideMenuLists();
            _menu = GameItems;
            MenuStore.Text = GameSubtitle(tile);
            RefreshGameItems(tile);
            ShowMenuItems(opener);
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
            : ReferenceEquals(_menu, TvItems) ? TvGroup
            : ReferenceEquals(_menu, OwnedItems) ? OwnedGroup
            : ReferenceEquals(_menu, DealsItems) ? DealsGroup
            : ReferenceEquals(_menu, AddItems) ? AddProgramGroup
            : ReferenceEquals(_menu, WhilePlayingItems) ? WhilePlayingGroup
            : ReferenceEquals(_menu, EmulatorItems) ? EmulatorsGroup
            : ReferenceEquals(_menu, MaintenanceItems) ? MaintenanceGroup
            : null;

        // A group goes back to its category, which is the list its row is in.
        if (row?.Parent is StackPanel category)
        {
            _confirming = null;
            OpenSettingsCategory(category, row);
            return true;
        }

        row = ReferenceEquals(_menu, LibraryDisplayItems) ? LibraryDisplayGroup
            : ReferenceEquals(_menu, GamesAppsItems) ? GamesAppsGroup
            : ReferenceEquals(_menu, SleepTweaksItems) ? SleepTweaksGroup
            : null;

        // One of the three lists opened from its own Old School card has no settings list behind it:
        // Back turns the card over again.
        if (row is null || _menuFromCard)
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
        UpdateSetting.Tag = _update is null ? null : Updates.Display(_update);

        BackgroundSetting.Tag = _background switch
        {
            BackgroundKind.Artwork => "Game artwork",
            BackgroundKind.Green => "Green",
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

        RefreshLayoutSettings();
        RefreshOwnedSettings();

        StatusSetting.Tag = OnOff(_showStatus);
        FilterSetting.Tag = OnOff(_showFilter);
        QuickActionsSetting.Tag = OnOff(_quickActions);
        SortSetting.Tag = SortName(_sort);
        HiddenSetting.Tag = OnOff(_showHidden);

        // Where to get a key is on the screen it is typed in.
        ArtworkKeySetting.Visibility = _background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkKeySetting.Tag = LibrarySettings.ArtworkKey is null ? "Not set" : "Set";
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
            Notice = "Handheld Optimiser is already open.";
            return;
        }

        if (MainApp.Start() is { } problem)
        {
            Program.Log($"Could not open the main app from settings: {problem}");
            Notice = $"Handheld Optimiser was not opened. {problem}";
            return;
        }

        Program.Log("Opened the main app from settings");
        Notice = "Opening Handheld Optimiser...";
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

        if (StepToolsSetting(row, step) || StepLayoutSetting(row, step))
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
            "Free at steamgriddb.com, under Preferences, then API. Game names are sent to SteamGridDB to find artwork. Leave empty to remove it.",
            LibrarySettings.ArtworkKey);
}
