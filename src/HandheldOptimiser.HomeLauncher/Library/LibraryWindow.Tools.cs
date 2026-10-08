using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The While playing and Maintenance settings: background programs closed around games, the stores' own
/// apps, clean-ups and the startup apps, so none of it needs the desktop.
/// </summary>
public partial class LibraryWindow
{
    // Rows added to a list when it opens, removed again before it is filled the next time.
    private readonly Dictionary<StackPanel, List<Button>> _addedRows = [];

    // Where the startup apps list goes back to: it opens from Maintenance and from While playing.
    private Button? _startupFrom;

    // Bumped each time a list is filled in the background, so a slow, older fill does not land in a newer list.
    private int _listing;

    /// <summary>Takes away the rows a list was filled with last time.</summary>
    private void ClearRows(StackPanel list)
    {
        foreach (var row in _addedRows.GetValueOrDefault(list) ?? [])
        {
            list.Children.Remove(row);
        }

        _addedRows[list] = [];
    }

    /// <summary>Adds a row to a list, above the given row.</summary>
    private Button AddRow(StackPanel list, UIElement before, string content, string? detail, Action click)
    {
        var row = new Button
        {
            Style = (Style)FindResource("MenuButton"),
            Content = content,
            Tag = detail
        };

        row.Click += (_, _) => click();
        list.Children.Insert(list.Children.IndexOf(before), row);
        (_addedRows.TryGetValue(list, out var rows) ? rows : _addedRows[list] = []).Add(row);
        return row;
    }

    // ----- While playing -----

    private void OnOpenWhilePlaying(object sender, RoutedEventArgs e) => OpenWhilePlaying();

    private void OpenWhilePlaying(Button? focus = null)
    {
        HideMenuLists();
        _menu = WhilePlayingItems;
        MenuTitle.Text = "While playing";
        MenuStore.Text = "Settings";
        RefreshWhilePlaying();
        ShowMenuItems(focus);
    }

    private void RefreshWhilePlaying()
    {
        RefreshQuickResumeSetting();
        RefreshResumeSetting();

        var chosen = BackgroundApps.Choices().Where(c => c.Close).Select(c => c.App.Name).ToList();
        var gameMode = LibrarySettings.GameMode;

        GameModeSetting.Tag = gameMode
            ? $"On. Closed as a game starts. A game's profile can change this"
            : "Off. A game's profile can switch it on for that game";

        AppsToCloseGroup.Tag = chosen.Count switch
        {
            0 => "None chosen",
            <= 3 => string.Join(", ", chosen),
            _ => $"{string.Join(", ", chosen.Take(2))} and {chosen.Count - 2} more"
        };

        ReopenSetting.Tag = LibrarySettings.ReopenApps ? "On, once the game has closed" : "Off. They stay closed";

        StoreClosingSetting.Tag = LibrarySettings.StoreClosing switch
        {
            StoreClosing.OpenedByGame => "When the game opened it. Steam, Epic, EA and the rest",
            StoreClosing.Always => "Always, even if it was open before the game",
            _ => "Off"
        };
    }

    private void OnToggleGameMode(object sender, RoutedEventArgs e)
    {
        LibrarySettings.GameMode = !LibrarySettings.GameMode;
        RefreshWhilePlaying();
    }

    private void OnToggleReopen(object sender, RoutedEventArgs e)
    {
        LibrarySettings.ReopenApps = !LibrarySettings.ReopenApps;
        RefreshWhilePlaying();
    }

    private void OnStepStoreClosing(object sender, RoutedEventArgs e) => StepStoreClosing(1);

    private void StepStoreClosing(int step)
    {
        LibrarySettings.StoreClosing = Step(LibrarySettings.StoreClosing, step);
        RefreshWhilePlaying();
    }

    // ----- Programs to close -----

    private void OnOpenAppsToClose(object sender, RoutedEventArgs e) => OpenAppsToClose();

    private void OpenAppsToClose(Button? focus = null)
    {
        HideMenuLists();
        _menu = AppsToCloseItems;
        MenuTitle.Text = "Programs to close";
        MenuStore.Text = "Only programs running as you. Windows itself is never touched";

        ClearRows(AppsToCloseItems);
        foreach (var (app, close) in BackgroundApps.Choices())
        {
            Button? row = null;
            var on = close;
            row = AddRow(AppsToCloseItems, AddRunningAppItem, app.Name, AppDetail(app, on), () =>
            {
                if (BackgroundApps.SetClose(app, !on))
                {
                    on = !on;
                    row!.Tag = AppDetail(app, on);
                }
            });
        }

        ShowMenuItems(focus);
    }

    private static string AppDetail(BackgroundApp app, bool close) =>
        !close ? "Left running"
        : app.ReopenArguments is null ? "Closed while playing. Windows starts it again when needed"
        : "Closed while playing";

    private void OnOpenRunningApps(object sender, RoutedEventArgs e)
    {
        HideMenuLists();
        _menu = RunningAppsItems;
        MenuTitle.Text = "Add a running program";
        MenuStore.Text = "Looking at what is running...";
        ClearRows(RunningAppsItems);
        ShowMenuItems();
        _ = ListRunningAppsAsync();
    }

    private async Task ListRunningAppsAsync()
    {
        var listing = ++_listing;
        var running = await Task.Run(BackgroundApps.Running);
        if (listing != _listing || !ReferenceEquals(_menu, RunningAppsItems))
        {
            return;
        }

        MenuStore.Text = running.Count == 0 ? "No other programs are running" : "Closed whenever a game starts, from the next game on";
        foreach (var (title, exe) in running)
        {
            AddRow(RunningAppsItems, RunningAppsBackItem, title, exe, () =>
            {
                StatusText.Text = BackgroundApps.AddCustom(exe, title) ? $"{title} will be closed while you play." : $"{title} could not be added.";
                OpenAppsToClose();
            });
        }

        ShowMenuItems();
    }

    // ----- Close background programs, from quick settings -----

    private void RefreshCloseAppsNow()
    {
        var count = BackgroundApps.Choices().Count(c => c.Close);
        CloseAppsNowItem.IsEnabled = count > 0;
        CloseAppsNowItem.Tag = count == 0 ? "Choose them under Settings, While playing"
            : "The ones chosen under Settings, While playing. They stay closed";
    }

    private async void OnCloseAppsNow(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        StatusText.Text = "Closing background programs...";
        var closed = await Task.Run(() => BackgroundApps.CloseChosen([]));
        StatusText.Text = closed.Count == 0 ? "None of the chosen programs were running."
            : $"Closed {string.Join(", ", closed.Select(c => c.Name))}.";
    }

    // ----- Sort -----

    private void OnStepSort(object sender, RoutedEventArgs e) => StepSort(1);

    private void StepSort(int step)
    {
        _sort = Step(_sort, step);
        LibrarySettings.Sort = _sort;
        RefreshSettings();
        ApplyFilter();
    }

    private static string SortName(SortOrder sort) => sort switch
    {
        SortOrder.MostPlayed => "Most played",
        SortOrder.Name => "A to Z",
        _ => "Recently played"
    };

    // ----- Maintenance -----

    private void OnOpenMaintenance(object sender, RoutedEventArgs e) => OpenMaintenance();

    private void OpenMaintenance(Button? focus = null)
    {
        HideMenuLists();
        _menu = MaintenanceItems;
        _confirming = null;
        MenuTitle.Text = "Maintenance";
        MenuStore.Text = StorageSummary();

        TempItem.Tag = ShaderItem.Tag = RecycleItem.Tag = "Measuring...";
        StartupGroup.Tag = "Programs that start with Windows";
        ShowMenuItems(focus);
        _ = MeasureAsync();
    }

    private (long Temp, long Shaders, long? Recycle)? _measured;

    private async Task MeasureAsync()
    {
        var listing = ++_listing;
        var measured = await Task.Run(() => (Cleanup.TempSize(), Cleanup.SizeOf(Cleanup.ShaderFolders), Cleanup.RecycleBinSize()));
        var startup = await Task.Run(StartupApps.Read);
        if (listing != _listing || !ReferenceEquals(_menu, MaintenanceItems))
        {
            return;
        }

        _measured = measured;
        var on = startup.Count(a => a.Enabled);
        StartupGroup.Tag = on == 1 ? "1 program starts with Windows" : $"{on} programs start with Windows";
        RefreshMaintenance();
    }

    private void RefreshMaintenance()
    {
        const string confirm = "Press again to confirm";
        if (_measured is not { } measured)
        {
            return;
        }

        TempItem.Tag = ReferenceEquals(_confirming, TempItem) ? confirm
            : $"{CleanResult.Size(measured.Temp)} left behind by programs and installers";
        ShaderItem.Tag = ReferenceEquals(_confirming, ShaderItem) ? confirm
            : $"{CleanResult.Size(measured.Shaders)}. Fixes stutter after a driver update. Games rebuild them as they play";
        RecycleItem.Tag = ReferenceEquals(_confirming, RecycleItem) ? confirm
            : measured.Recycle is { } bin ? CleanResult.Size(bin) : "Size unknown";
    }

    /// <summary>Free space on each drive: "C: 120 GB free of 476 GB".</summary>
    private static string StorageSummary()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => $"{d.Name.TrimEnd('\\')} {CleanResult.Size(d.AvailableFreeSpace)} free of {CleanResult.Size(d.TotalSize)}");
            return string.Join("   ", drives);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Settings";
        }
    }

    private void OnClearTemp(object sender, RoutedEventArgs e) =>
        ConfirmClean(TempItem, "Temporary files", Cleanup.ClearTemp);

    private void OnClearShaders(object sender, RoutedEventArgs e) =>
        ConfirmClean(ShaderItem, "Shader caches", () => Cleanup.Clear(Cleanup.ShaderFolders));

    private void OnEmptyRecycleBin(object sender, RoutedEventArgs e) =>
        ConfirmClean(RecycleItem, "Recycle Bin", () => Cleanup.EmptyRecycleBin()
            ? new CleanResult(1, _measured?.Recycle ?? 0, 0)
            : new CleanResult(0, 0, 0));

    /// <summary>Arms a clean-up row on its first press and runs it on the second.</summary>
    private async void ConfirmClean(Button row, string name, Func<CleanResult> clean)
    {
        if (!ReferenceEquals(_confirming, row))
        {
            _confirming = row;
            RefreshMaintenance();
            return;
        }

        _confirming = null;
        row.Tag = "Clearing...";
        var result = await Task.Run(clean);
        Program.Log($"{name}: {result.Describe()}");
        StatusText.Text = $"{name}: {result.Describe()}.";

        if (ReferenceEquals(_menu, MaintenanceItems))
        {
            MenuStore.Text = StorageSummary();
            _ = MeasureAsync();
        }
    }

    /// <summary>Moving off an armed row disarms it, so the second press is always a deliberate one.</summary>
    private void OnConfirmRowLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(_confirming, sender))
        {
            _confirming = null;
            if (sender is Button row && _confirmDetails.TryGetValue(row, out var detail))
            {
                row.Tag = detail;
            }
            else
            {
                RefreshMaintenance();
            }
        }
    }

    // ----- Startup apps -----

    private void OnOpenStartup(object sender, RoutedEventArgs e) => OpenStartup(StartupGroup);

    private void OnOpenStoreStartup(object sender, RoutedEventArgs e) => OpenStartup(StoreStartupItem);

    private void OpenStartup(Button from)
    {
        _startupFrom = from;
        HideMenuLists();
        _menu = StartupItems;
        MenuTitle.Text = "Startup apps";
        MenuStore.Text = "Takes effect the next time you sign in";
        StartupBackItem.Tag = ReferenceEquals(from, StoreStartupItem) ? "While playing" : "Maintenance";

        ClearRows(StartupItems);
        var apps = StartupApps.Read();
        if (apps.Count == 0)
        {
            MenuStore.Text = "Nothing starts with Windows";
        }

        foreach (var app in apps)
        {
            var current = app;
            Button? row = null;
            row = AddRow(StartupItems, StartupBackItem, app.Name, StartupDetail(app), () =>
            {
                if (StartupApps.SetEnabled(current, !current.Enabled))
                {
                    current = current with { Enabled = !current.Enabled };
                    row!.Tag = StartupDetail(current);
                }
            });

            // Everyone's entries need administrator rights, which the main app has.
            row.IsEnabled = !app.ForAllUsers;
        }

        // The store apps are first in the list, so that is where the focus starts.
        ShowMenuItems();
    }

    private static string StartupDetail(StartupApp app) =>
        (app.Enabled ? "Starts with Windows" : "Off")
        + (app.ForAllUsers ? ". Set for all users: change it in Handheld Optimiser" : string.Empty);

    /// <summary>Left and right on the rows of these settings.</summary>
    private bool StepToolsSetting(Button row, int step)
    {
        if (ReferenceEquals(row, StoreClosingSetting))
        {
            StepStoreClosing(step);
            return true;
        }

        if (ReferenceEquals(row, SortSetting))
        {
            StepSort(step);
            return true;
        }

        return false;
    }
}
