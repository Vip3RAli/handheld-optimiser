using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The library's own settings, from the gear in the top bar or the controller's Menu button: a short list
/// of three categories, each a short list of groups. This class has the settings list, Library display's
/// groups (the background, what the library shows and docked mode), and where every group goes Back to.
/// The other groups are each their own menu's, which wires the row that opens it.
/// </summary>
internal sealed class SettingsMenu
{
    // The categories the settings list opens, each a short list of groups. A group's list names its
    // category under its title.
    public const string LibraryDisplay = "Appearance & Layout";
    public const string GamesApps = "Catalog & Content";
    public const string SleepTweaks = "Performance & Power";

    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly BackdropPresenter _backdrop;
    private readonly TileArtwork _artwork;
    private readonly TopBarPresenter _topBar;
    private readonly ContinuePresenter _continue;
    private readonly UIElement _optionsHint;
    private readonly ILibraryHost _host;

    /// <param name="optionsHint">The X Options hint at the foot of the screen, which shows only while quick actions are on.</param>
    public SettingsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, LibraryOptions options,
        BackdropPresenter backdrop, TileArtwork artwork, TopBarPresenter topBar, ContinuePresenter continuePresenter,
        UIElement optionsHint, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _options = options;
        _backdrop = backdrop;
        _artwork = artwork;
        _topBar = topBar;
        _continue = continuePresenter;
        _optionsHint = optionsHint;
        _host = host;

        // The settings list.
        view.UpdateSetting.Click += (_, _) => topBar.UpdateNow();
        view.LibraryDisplayGroup.Click += (_, _) => OpenCategory(view.LibraryDisplayItems);
        view.GamesAppsGroup.Click += (_, _) => OpenCategory(view.GamesAppsItems);
        view.SleepTweaksGroup.Click += (_, _) => OpenCategory(view.SleepTweaksItems);
        view.RescanItem.Click += (_, _) => Rescan();
        view.QuitSetting.Click += (_, _) => Quit();
        view.OpenMainAppSetting.Click += (_, _) => OpenMainApp();

        // Appearance & Layout's groups.
        view.BackgroundGroup.Click += (_, _) => _menu.Show(view.BackgroundItems, "Artwork & Theme", LibraryDisplay);
        view.DisplayGroup.Click += (_, _) => _menu.Show(view.DisplayItems, "Library Elements", LibraryDisplay);
        view.TvGroup.Click += (_, _) => _menu.Show(view.TvItems, "Docked Display",
            _host.Docked ? "The library is on a TV or monitor now" : "The library is on the built-in screen now");

        // Art and background.
        view.BackgroundSetting.Click += (_, _) => StepBackground(1);
        view.StrengthSetting.Click += (_, _) => StepStrength(1);
        view.BlurSetting.Click += (_, _) => StepBlur(1);
        view.PositionSetting.Click += (_, _) => StepPosition(1);
        view.ArtworkKeySetting.Click += (_, _) => EditArtworkKey();

        // What it shows.
        view.LayoutSetting.Click += (_, _) => StepLayout(1);
        view.ContinueSetting.Click += (_, _) =>
        {
            _options.ContinuePlaying = !_options.ContinuePlaying;
            Refresh();
            _continue.Update();
        };
        view.ContinueInfoSetting.Click += (_, _) =>
        {
            _options.ContinueInfo = !_options.ContinueInfo;
            Refresh();
            _continue.Update();
        };
        view.StoreDetailsSetting.Click += (_, _) =>
        {
            _options.StoreDetails = !_options.StoreDetails;
            Refresh();

            // Asked for again, or taken away, for the game on the card.
            _continue.ForgetInfo();
            _continue.Update();
        };
        view.StatusSetting.Click += (_, _) =>
        {
            _options.ShowStatus = !_options.ShowStatus;
            Refresh();
            _ = _topBar.UpdateStatusAsync();
        };
        view.FilterSetting.Click += (_, _) =>
        {
            _options.ShowFilter = !_options.ShowFilter;
            Refresh();

            // Switching it off also drops a store filter that could no longer be changed.
            _host.ShowTiles();
        };
        view.QuickActionsSetting.Click += (_, _) =>
        {
            _options.QuickActions = !_options.QuickActions;
            _optionsHint.Visibility = _options.QuickActions ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
        view.HiddenSetting.Click += (_, _) =>
        {
            _options.ShowHidden = !_options.ShowHidden;
            Refresh();
            _host.ShowTiles();
        };
        view.SortSetting.Click += (_, _) => StepSort(1);

        // Docked mode.
        view.DockedSetting.Click += (_, _) =>
        {
            LibrarySettings.DockedMode = !LibrarySettings.DockedMode;
            LayoutChanged();
        };
        view.DockedLayoutSetting.Click += (_, _) => StepDockedLayout(1);
        view.DockedSizeSetting.Click += (_, _) => StepDockedSize(1);

        foreach (var back in new[]
        {
            view.LibraryDisplaySettingsBackItem, view.GamesAppsSettingsBackItem, view.SleepTweaksSettingsBackItem,
            view.BackgroundSettingsBackItem, view.DisplaySettingsBackItem, view.TvSettingsBackItem
        })
        {
            back.Click += (_, _) => menu.Back();
        }

        RegisterBacks();
        menu.AddStepper(Step);
    }

    /// <summary>The settings list.</summary>
    public void Open()
    {
        if (_menu.Open(_view.SettingsItems, "Settings", "Game library"))
        {
            Refresh();
        }
    }

    /// <summary>One category's groups, in place of the settings list.</summary>
    private void OpenCategory(StackPanel category, Button? focus = null)
    {
        var title = ReferenceEquals(category, _view.LibraryDisplayItems) ? LibraryDisplay
            : ReferenceEquals(category, _view.GamesAppsItems) ? GamesApps
            : SleepTweaks;

        _menu.Show(category, title, "Settings", focus);
    }

    /// <summary>
    /// One of the three categories, opened from its own Old School card, which turns over to show it. Back
    /// turns it over again, as there is no settings list behind it.
    /// </summary>
    public void OpenCategoryFromCard(StackPanel category)
    {
        Open();
        if (_menu.List is null)
        {
            return;
        }

        OpenCategory(category);
        _menu.FromCard = true;
    }

    /// <summary>
    /// Where each list goes Back to: a group of settings to its category with that group's row focused, and a
    /// category to the settings list. The settings list, the power menu and quick settings go nowhere, so Back
    /// closes the menu.
    /// </summary>
    private void RegisterBacks()
    {
        var view = _view;

        // A group goes back to its category, which is the list its row is in.
        foreach (var (group, row) in new[]
        {
            (view.BackgroundItems, view.BackgroundGroup), (view.DisplayItems, view.DisplayGroup), (view.TvItems, view.TvGroup),
            (view.OwnedItems, view.OwnedGroup), (view.DealsItems, view.DealsGroup), (view.AddItems, view.AddProgramGroup),
            (view.WhilePlayingItems, view.WhilePlayingGroup), (view.EmulatorItems, view.EmulatorsGroup),
            (view.MaintenanceItems, view.MaintenanceGroup)
        })
        {
            _menu.SetBack(group, () =>
            {
                if (row.Parent is not StackPanel category)
                {
                    return false;
                }

                _menu.Armed = null;
                OpenCategory(category, row);
                return true;
            });
        }

        // A category goes back to the settings list. One opened from its own Old School card has no
        // settings list behind it: Back turns the card over again.
        foreach (var (category, row) in new[]
        {
            (view.LibraryDisplayItems, view.LibraryDisplayGroup), (view.GamesAppsItems, view.GamesAppsGroup),
            (view.SleepTweaksItems, view.SleepTweaksGroup)
        })
        {
            _menu.SetBack(category, () =>
            {
                if (_menu.FromCard)
                {
                    return false;
                }

                _menu.Armed = null;
                _menu.Show(view.SettingsItems, "Settings", "Game library", row);
                return true;
            });
        }
    }

    private void Refresh()
    {
        var view = _view;
        view.UpdateSetting.Visibility = _topBar.Update is null ? Visibility.Collapsed : Visibility.Visible;
        view.UpdateSetting.Tag = _topBar.Update is null ? null : Updates.Display(_topBar.Update);

        view.BackgroundSetting.Tag = _options.Background switch
        {
            BackgroundKind.Artwork => "Game artwork",
            BackgroundKind.Green => "Green",
            BackgroundKind.Plain => "Plain",
            _ => "Game colours"
        };

        // Artwork mode still shows colours for a game with no artwork, so the strength applies there too.
        view.StrengthSetting.IsEnabled = _options.Background != BackgroundKind.Plain;
        view.StrengthSetting.Tag = _options.Strength.ToString();

        view.BlurSetting.Visibility = _options.Background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        view.BlurSetting.Tag = _options.Blur.ToString();

        view.PositionSetting.Visibility = view.BlurSetting.Visibility;
        view.PositionSetting.Tag = _options.Position.ToString();

        view.LayoutSetting.Tag = LibrarySettings.Layout switch
        {
            LibraryLayout.Row => "Row",
            LibraryLayout.OldSchool => "Old School",
            _ => "Grid"
        };
        view.ContinueSetting.Tag = SettingSteps.OnOff(_options.ContinuePlaying);

        view.ContinueInfoSetting.IsEnabled = _options.ContinuePlaying;
        view.ContinueInfoSetting.Tag = SettingSteps.OnOff(_options.ContinueInfo);

        view.StoreDetailsSetting.IsEnabled = _options.ContinuePlaying && _options.ContinueInfo;
        view.StoreDetailsSetting.Tag = SettingSteps.OnOff(_options.StoreDetails);

        var dockedMode = LibrarySettings.DockedMode;
        view.DockedSetting.Tag = SettingSteps.OnOff(dockedMode);

        view.DockedLayoutSetting.IsEnabled = dockedMode;
        view.DockedLayoutSetting.Tag = LibrarySettings.DockedLayout switch
        {
            DockedLayout.Grid => "Grid",
            DockedLayout.OldSchool => "Old School",
            _ => "Row"
        };

        view.DockedSizeSetting.IsEnabled = dockedMode;
        view.DockedSizeSetting.Tag = LibrarySettings.DockedSize is var size and > 0
            ? $"{size}%"
            : "Fit to the screen";

        view.StatusSetting.Tag = SettingSteps.OnOff(_options.ShowStatus);
        view.FilterSetting.Tag = SettingSteps.OnOff(_options.ShowFilter);
        view.QuickActionsSetting.Tag = SettingSteps.OnOff(_options.QuickActions);
        view.SortSetting.Tag = _options.Sort switch
        {
            SortOrder.MostPlayed => "Most played",
            SortOrder.Name => "A to Z",
            _ => "Recently played"
        };
        view.HiddenSetting.Tag = SettingSteps.OnOff(_options.ShowHidden);

        // Where to get a key is on the screen it is typed in.
        view.ArtworkKeySetting.Visibility = _options.Background == BackgroundKind.Artwork ? Visibility.Visible : Visibility.Collapsed;
        view.ArtworkKeySetting.Tag = LibrarySettings.ArtworkKey is null ? "Not set" : "Set";
    }

    // ----- Art and background -----

    private void StepBackground(int step)
    {
        _options.Background = SettingSteps.Step(_options.Background, step);
        Refresh();
        _backdrop.Reload();
    }

    private void StepStrength(int step)
    {
        _options.Strength = SettingSteps.Step(_options.Strength, step);
        Refresh();
        _backdrop.Reload();
    }

    private void StepPosition(int step)
    {
        _options.Position = SettingSteps.Step(_options.Position, step);
        Refresh();
        _backdrop.Reload();
    }

    private void StepBlur(int step)
    {
        _options.Blur = SettingSteps.Step(_options.Blur, step);
        Refresh();
        _backdrop.Reload();
    }

    private void EditArtworkKey() =>
        _menu.BeginEdit("SteamGridDB API key",
            "Free at steamgriddb.com, under Preferences, then API. Game names are sent to SteamGridDB to find artwork. Leave empty to remove it.",
            LibrarySettings.ArtworkKey, text =>
            {
                // Back to the settings list, with the focused game's artwork looked up under the new key,
                // and the covers that the old key (or having none) could not get.
                LibrarySettings.ArtworkKey = text;
                Refresh();
                _menu.ShowItems();
                _backdrop.Reload();
                _artwork.KeyChanged();
            });

    // ----- The layout, its order, and docked mode -----

    private void StepLayout(int step)
    {
        LibrarySettings.Layout = SettingSteps.Step(LibrarySettings.Layout, step);
        LayoutChanged();
    }

    private void StepSort(int step)
    {
        _options.Sort = SettingSteps.Step(_options.Sort, step);
        Refresh();
        _host.ShowTiles();
    }

    private void StepDockedLayout(int step)
    {
        LibrarySettings.DockedLayout = SettingSteps.Step(LibrarySettings.DockedLayout, step);
        LayoutChanged();
    }

    private void StepDockedSize(int step)
    {
        var sizes = LibrarySettings.DockedSizes;
        var index = Array.IndexOf(sizes, LibrarySettings.DockedSize);
        LibrarySettings.DockedSize = sizes[(Math.Max(index, 0) + step + sizes.Length) % sizes.Length];
        LayoutChanged();
    }

    private void LayoutChanged()
    {
        Refresh();
        _host.LayoutSettingsChanged();
    }

    /// <summary>Left and right on the rows of Art and background, What it shows and Docked mode.</summary>
    private bool Step(Button row, int step)
    {
        var view = _view;
        if (ReferenceEquals(row, view.BackgroundSetting))
        {
            StepBackground(step);
        }
        else if (ReferenceEquals(row, view.StrengthSetting))
        {
            StepStrength(step);
        }
        else if (ReferenceEquals(row, view.PositionSetting))
        {
            StepPosition(step);
        }
        else if (ReferenceEquals(row, view.BlurSetting))
        {
            StepBlur(step);
        }
        else if (ReferenceEquals(row, view.LayoutSetting))
        {
            StepLayout(step);
        }
        else if (ReferenceEquals(row, view.SortSetting))
        {
            StepSort(step);
        }
        else if (ReferenceEquals(row, view.DockedLayoutSetting))
        {
            StepDockedLayout(step);
        }
        else if (ReferenceEquals(row, view.DockedSizeSetting))
        {
            StepDockedSize(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    // ----- Refresh, the main app, and closing the library -----

    /// <summary>Looks for games again, from the settings list or the Old School layout's Refresh library card.</summary>
    public void Rescan()
    {
        _menu.Close();
        _notices.Show("Looking for games...");
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        // An explicit refresh also looks for emulators installed meanwhile.
        await _host.ScanAsync(refreshEmulators: true);
        _notices.Show(_state.AllTiles.Count == 1 ? "1 game in the library." : $"{_state.AllTiles.Count} games in the library.");
    }

    /// <summary>
    /// Opens the main app over the library, which stays open behind it. If it is already open it is
    /// brought to the front instead, with no second copy and no administrator prompt.
    /// </summary>
    private void OpenMainApp()
    {
        _menu.Close();

        if (MainApp.ShowIfRunning())
        {
            Program.Log("Switched to the main app, which was already open");
            _notices.Show("Handheld Optimiser is already open.");
            return;
        }

        if (MainApp.Start() is { } problem)
        {
            Program.Log($"Could not open the main app from settings: {problem}");
            _notices.Show($"Handheld Optimiser was not opened. {problem}");
            return;
        }

        Program.Log("Opened the main app from settings");
        _notices.Show("Opening Handheld Optimiser...");
    }

    /// <summary>Closes the library, from its settings or its Old School card. Windows starts it again on the next home button press.</summary>
    public void Quit()
    {
        Program.Log("Game library closed from its settings");
        _host.Window.Close();
    }
}
