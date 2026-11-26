using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The library's own settings, from the gear in the top bar or the controller's Menu button: a short list
/// of five categories, each a short list of settings and of groups of them. The user had them regrouped on
/// 2026-11-15: Look and Layout, Home Screen, Games and Stores, While Playing, and System. This class has the
/// settings list, the rows of Look and Layout and Home Screen, the API Keys list (whose rows are still their
/// own menus'), and where every group goes Back to. The other groups are each their own menu's, which
/// wires the row that opens it.
/// </summary>
internal sealed class SettingsMenu
{
    // The categories the settings list opens, each a short list of groups. A group's list names its
    // category under its title.
    public const string LibraryDisplay = "Look & Layout";
    public const string HomeScreen = "Home Screen";
    public const string GamesApps = "Games & Stores";
    public const string SleepTweaks = "While Playing";
    public const string System = "System";

    /// <summary>Told as the API Keys list opens, so the menus its rows belong to can say what each is set to.</summary>
    public event Action? ApiKeysOpening;

    /// <summary>A Home Screen setting that the deals below the games follow has changed.</summary>
    public event Action? HomeScreenChanged;

    /// <summary>A layout's name as the menu shows it.</summary>
    private static string LayoutName(LibraryLayout layout) => layout switch
    {
        LibraryLayout.Row => "Row",
        LibraryLayout.OldSchool => "NXE",
        _ => "Grid"
    };

    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly LibraryOptions _options;
    private readonly BackdropPresenter _backdrop;
    private readonly TileArtwork _artwork;
    private readonly TopBarPresenter _topBar;
    private readonly ContinuePresenter _continue;
    private readonly UIElement _hintBar;
    private readonly ILibraryHost _host;

    /// <param name="hintBar">The controller button hint bar at the foot of the screen, hidden when hints are off.</param>
    public SettingsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, LibraryOptions options,
        BackdropPresenter backdrop, TileArtwork artwork, TopBarPresenter topBar, ContinuePresenter continuePresenter,
        UIElement hintBar, ILibraryHost host)
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
        _hintBar = hintBar;
        _host = host;

        // The settings list.
        view.UpdateSetting.Click += (_, _) => topBar.UpdateNow();
        view.LibraryDisplayGroup.Click += (_, _) => OpenCategory(view.LibraryDisplayItems);
        view.GamesAppsGroup.Click += (_, _) => OpenCategory(view.GamesAppsItems);
        view.SleepTweaksGroup.Click += (_, _) => OpenCategory(view.SleepTweaksItems);
        view.RescanItem.Click += (_, _) => Rescan();
        view.QuitSetting.Click += (_, _) => Quit();
        view.OpenMainAppSetting.Click += (_, _) => OpenMainApp();

        view.DisplayGroup.Click += (_, _) => OpenCategory(view.DisplayItems);
        view.SystemGroup.Click += (_, _) => OpenCategory(view.SystemItems);

        // Look & Layout's groups.
        view.BackgroundGroup.Click += (_, _) => _menu.Show(view.BackgroundItems, "Background & Artwork", LibraryDisplay);

        // Every API key, each row its own menu's.
        view.ApiKeysGroup.Click += (_, _) =>
        {
            Refresh();
            ApiKeysOpening?.Invoke();
            _menu.Show(view.ApiKeysItems, "API Keys", GamesApps);
        };
        view.TvGroup.Click += (_, _) => _menu.Show(view.TvItems, "Docked Display",
            _host.Docked ? "The library is on a TV or monitor now" : "The library is on the built-in screen now");

        // Art and background.
        view.BackgroundSetting.Click += (_, _) => StepBackground(1);
        view.StrengthSetting.Click += (_, _) => StepStrength(1);
        view.BlurSetting.Click += (_, _) => StepBlur(1);
        view.PositionSetting.Click += (_, _) => StepPosition(1);
        view.ArtworkKeySetting.Click += (_, _) => EditArtworkKey();
        view.OldSchoolLookSetting.Click += (_, _) => StepOldSchoolLook();
        view.MovingBackgroundSetting.Click += (_, _) =>
        {
            LibrarySettings.MovingBackground = !LibrarySettings.MovingBackground;
            Refresh();
            _host.OldSchoolLookChanged();
        };

        // What it shows.
        view.LayoutSetting.Click += (_, _) => StepLayout(1);
        view.ContinueSetting.Click += (_, _) =>
        {
            _options.ContinuePlaying = !_options.ContinuePlaying;
            Refresh();

            // The games around it change too: the front card in NXE, a favourite back among the others.
            _host.ShowTiles();
        };
        view.ContinueInfoSetting.Click += (_, _) =>
        {
            _options.ContinueInfo = !_options.ContinueInfo;
            Refresh();
            _continue.Update();

            // The last game's play time and achievements on its picture in the grid.
            _host.ShowTiles();
        };
        view.StoreDetailsSetting.Click += (_, _) =>
        {
            _options.StoreDetails = !_options.StoreDetails;
            Refresh();

            // Asked for again, or taken away, for the game on the card.
            _continue.ForgetInfo();
            _continue.Update();
        };
        view.CardDetailsSetting.Click += (_, _) => StepCardDetails(1);
        view.StoreBadgeSetting.Click += (_, _) =>
        {
            LibrarySettings.StoreBadge = !LibrarySettings.StoreBadge;
            Refresh();
            HomeScreenChanged?.Invoke();
        };
        view.TitlesSetting.Click += (_, _) =>
        {
            LibrarySettings.SectionTitles = !LibrarySettings.SectionTitles;
            Refresh();
            _continue.Update();
            HomeScreenChanged?.Invoke();
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
            _hintBar.Visibility = _options.QuickActions ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        };
        view.HiddenSetting.Click += (_, _) =>
        {
            _options.ShowHidden = !_options.ShowHidden;
            Refresh();
            _host.ShowTiles();
        };
        view.HiddenGamesGroup.Click += (_, _) => OpenHiddenGames();
        view.HiddenGamesBackItem.Click += (_, _) => menu.Back();
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
            view.BackgroundSettingsBackItem, view.DisplaySettingsBackItem, view.TvSettingsBackItem,
            view.SystemBackItem, view.ApiKeysBackItem
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
            : ReferenceEquals(category, _view.DisplayItems) ? HomeScreen
            : ReferenceEquals(category, _view.GamesAppsItems) ? GamesApps
            : ReferenceEquals(category, _view.SystemItems) ? System
            : SleepTweaks;

        // A row hidden while its list was open, as Downloads is once the last download ends, cannot take the
        // focus, so the category's first row does.
        // Each layout has its own Home Screen settings, so the menu says whose they are.
        var subtitle = ReferenceEquals(category, _view.DisplayItems) ? $"{LayoutName(LibrarySettings.ShownLayout)} layout" : "Settings";
        _menu.Show(category, title, subtitle, focus is { Visibility: Visibility.Visible } ? focus : null);
    }

    /// <summary>
    /// One of the categories, opened from its own Old School card. Back closes the menu, as there is
    /// no settings list behind it.
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
            (view.BackgroundItems, view.BackgroundGroup), (view.TvItems, view.TvGroup), (view.ApiKeysItems, view.ApiKeysGroup),
            (view.BackgroundAppsItems, view.BackgroundAppsGroup), (view.PowerCoolingItems, view.PowerCoolingGroup),
            (view.StoreAppsItems, view.StoreAppsGroup),
            (view.AccountsItems, view.AccountsGroup), (view.OwnedItems, view.OwnedGroup), (view.DealsItems, view.DealsGroup),
            (view.HiddenGamesItems, view.HiddenGamesGroup),
            (view.AddItems, view.AddProgramGroup), (view.WhilePlayingItems, view.WhilePlayingGroup),
            (view.EmulatorItems, view.EmulatorsGroup), (view.MaintenanceItems, view.MaintenanceGroup),
            (view.DownloadsItems, view.DownloadsGroup)
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
            (view.LibraryDisplayItems, view.LibraryDisplayGroup), (view.DisplayItems, view.DisplayGroup),
            (view.GamesAppsItems, view.GamesAppsGroup), (view.SleepTweaksItems, view.SleepTweaksGroup), (view.SystemItems, view.SystemGroup)
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

        view.LayoutSetting.Tag = LayoutName(LibrarySettings.Layout);

        // Each layout shows its games' details its own way (2026-11-16): the grid on the last game's picture, the
        // row below the picked game with what its store says, and the NXE layout on the picked card, as chosen.
        var shown = LibrarySettings.ShownLayout;
        static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        view.ContinueInfoSetting.Visibility = Shown(shown != LibraryLayout.OldSchool);
        view.StoreDetailsSetting.Visibility = Shown(shown == LibraryLayout.Row);
        view.CardDetailsSetting.Visibility = Shown(shown == LibraryLayout.OldSchool);
        view.TitlesSetting.Visibility = Shown(shown != LibraryLayout.OldSchool);
        view.TitlesSetting.Tag = SettingSteps.OnOff(LibrarySettings.SectionTitles);
        view.StoreBadgeSetting.Tag = SettingSteps.OnOff(LibrarySettings.StoreBadge);
        view.ContinueSetting.Tag = SettingSteps.OnOff(_options.ContinuePlaying);

        // In the grid the details are on the last game's picture, so they need it first.
        view.ContinueInfoSetting.IsEnabled = shown == LibraryLayout.Row || _options.ContinuePlaying;
        view.ContinueInfoSetting.Tag = SettingSteps.OnOff(_options.ContinueInfo);

        view.StoreDetailsSetting.IsEnabled = _options.ContinueInfo;
        view.StoreDetailsSetting.Tag = SettingSteps.OnOff(_options.StoreDetails);
        view.CardDetailsSetting.Tag = LibrarySettings.CardDetails switch
        {
            CardDetails.Playtime => "Play time",
            CardDetails.Achievements => "Achievements",
            CardDetails.Both => "Both",
            _ => "Off"
        };

        var dockedMode = LibrarySettings.DockedMode;
        view.DockedSetting.Tag = SettingSteps.OnOff(dockedMode);

        view.DockedLayoutSetting.IsEnabled = dockedMode;
        view.DockedLayoutSetting.Tag = LibrarySettings.DockedLayout switch
        {
            DockedLayout.Grid => "Grid",
            DockedLayout.OldSchool => "NXE",
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
        var hiddenCount = _state.AllTiles.Count(t => t.IsHidden);
        view.HiddenSetting.Tag = SettingSteps.OnOff(_options.ShowHidden);
        view.HiddenGamesGroup.Visibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        view.HiddenGamesGroup.Tag = hiddenCount > 0 ? $"{hiddenCount} hidden" : null;

        // Keep SteamGridDB API key visible regardless of current background mode (also used by Old School cards and Apps).
        view.ArtworkKeySetting.Visibility = Visibility.Visible;
        view.ArtworkKeySetting.Tag = LibrarySettings.ArtworkKey is null ? "Not set" : "Set";

        // How the Old School layout is drawn, and whether the Console space look's background moves.
        // Both belong to the NXE layout (Old School in the code), so they show only while it is the layout
        // chosen, on the device's own screen or docked; the user asked for that on 2026-11-15.
        var nxe = LibrarySettings.Layout == LibraryLayout.OldSchool
            || (LibrarySettings.DockedMode && LibrarySettings.DockedLayout == DockedLayout.OldSchool);
        var console = LibrarySettings.OldSchoolLook == OldSchoolLook.ConsoleSpace;
        view.OldSchoolLookSetting.Visibility = nxe ? Visibility.Visible : Visibility.Collapsed;
        view.OldSchoolLookSetting.Tag = console ? "Modern" : "Classic";
        view.MovingBackgroundSetting.Visibility = view.OldSchoolLookSetting.Visibility;
        view.MovingBackgroundSetting.IsEnabled = console;
        view.MovingBackgroundSetting.Tag = SettingSteps.OnOff(LibrarySettings.MovingBackground);

        // The NXE look's animated background covers the player's own, so its settings go while it is on; with it
        // off the game's colours or artwork show behind the cards (2026-11-16).
        view.BackgroundGroup.Visibility = LibrarySettings.ShownLayout == LibraryLayout.OldSchool && console
            && LibrarySettings.MovingBackground ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Between the Classic look and Console space. There are only the two, so either way is the other.</summary>
    private void StepOldSchoolLook()
    {
        LibrarySettings.OldSchoolLook = LibrarySettings.OldSchoolLook == OldSchoolLook.ConsoleSpace
            ? OldSchoolLook.Classic
            : OldSchoolLook.ConsoleSpace;
        Refresh();
        _host.OldSchoolLookChanged();
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

    private void StepCardDetails(int step)
    {
        LibrarySettings.CardDetails = SettingSteps.Step(LibrarySettings.CardDetails, step);
        Refresh();
        _host.ShowTiles();
    }

    private void LayoutChanged()
    {
        _host.LayoutSettingsChanged();
        Refresh();
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
        else if (ReferenceEquals(row, view.OldSchoolLookSetting))
        {
            StepOldSchoolLook();
        }
        else if (ReferenceEquals(row, view.CardDetailsSetting))
        {
            StepCardDetails(step);
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
        else if (ReferenceEquals(row, view.MovingBackgroundSetting))
        {
            LibrarySettings.MovingBackground = !LibrarySettings.MovingBackground;
            Refresh();
            _host.OldSchoolLookChanged();
        }
        else if (ReferenceEquals(row, view.ContinueSetting))
        {
            _options.ContinuePlaying = !_options.ContinuePlaying;
            Refresh();
            _host.ShowTiles();
        }
        else if (ReferenceEquals(row, view.ContinueInfoSetting))
        {
            _options.ContinueInfo = !_options.ContinueInfo;
            Refresh();
            _continue.Update();
            _host.ShowTiles();
        }
        else if (ReferenceEquals(row, view.StoreDetailsSetting))
        {
            _options.StoreDetails = !_options.StoreDetails;
            Refresh();
            _continue.ForgetInfo();
            _continue.Update();
        }
        else if (ReferenceEquals(row, view.TitlesSetting))
        {
            LibrarySettings.SectionTitles = !LibrarySettings.SectionTitles;
            Refresh();
            _continue.Update();
            HomeScreenChanged?.Invoke();
        }
        else if (ReferenceEquals(row, view.StoreBadgeSetting))
        {
            LibrarySettings.StoreBadge = !LibrarySettings.StoreBadge;
            Refresh();
            HomeScreenChanged?.Invoke();
        }
        else if (ReferenceEquals(row, view.StatusSetting))
        {
            _options.ShowStatus = !_options.ShowStatus;
            Refresh();
            _ = _topBar.UpdateStatusAsync();
        }
        else if (ReferenceEquals(row, view.FilterSetting))
        {
            _options.ShowFilter = !_options.ShowFilter;
            Refresh();
            _host.ShowTiles();
        }
        else if (ReferenceEquals(row, view.QuickActionsSetting))
        {
            _options.QuickActions = !_options.QuickActions;
            _hintBar.Visibility = _options.QuickActions ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }
        else if (ReferenceEquals(row, view.HiddenSetting))
        {
            _options.ShowHidden = !_options.ShowHidden;
            Refresh();
            _host.ShowTiles();
        }
        else if (ReferenceEquals(row, view.DockedSetting))
        {
            LibrarySettings.DockedMode = !LibrarySettings.DockedMode;
            LayoutChanged();
        }
        else
        {
            return false;
        }

        return true;
    }

    private void OpenHiddenGames(string? focusKey = null)
    {
        var hidden = _state.AllTiles
            .Where(t => t.IsHidden)
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (hidden.Count == 0)
        {
            OpenCategory(_view.DisplayItems, _view.HiddenSetting);
            return;
        }

        _menu.ClearRows(_view.HiddenGamesItems);
        Button? focusMatch = null;
        foreach (var tile in hidden)
        {
            var target = tile;
            var row = _menu.AddRow(_view.HiddenGamesItems, _view.HiddenGamesBackItem, target.Title, "Restore", () =>
            {
                if (!GameCatalog.SetHidden(target.Game, false))
                {
                    _notices.Show($"{target.Title} could not be shown.");
                    return;
                }

                target.IsHidden = false;
                _host.ShowTiles();
                _notices.Show($"{target.Title} restored to the library.");
                Refresh();
                OpenHiddenGames();
            });
            MenuRow.SetAbout(row, $"Restore {target.Title} ({target.StoreName}) to the library.");
            if (focusKey is not null && target.Game.Key == focusKey)
            {
                focusMatch = row;
            }
        }

        _menu.Show(_view.HiddenGamesItems, "Hidden games", HomeScreen, focusMatch);
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
