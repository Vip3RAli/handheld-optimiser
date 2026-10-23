using System.IO;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Sleep and clean-up's two groups. Play and sleep: background programs closed around games, the stores'
/// own apps, TDP Engine, Quick Resume and going back to the game after sleep. Clean up: temporary files,
/// shader caches, the Recycle Bin and the startup apps, so none of it needs the desktop. The startup apps
/// open from both groups and go back to the one they came from.
/// </summary>
internal sealed class SleepAndCleanUpMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly ILibraryHost _host;

    // Where the startup apps list goes back to.
    private Button? _startupFrom;

    // Bumped each time a list is filled in the background, so a slow, older fill does not land in a newer list.
    private int _listing;

    private (long Temp, long Shaders, long? Recycle)? _measured;

    public SleepAndCleanUpMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _host = host;

        // Play and sleep.
        view.WhilePlayingGroup.Click += (_, _) => OpenWhilePlaying();
        view.GameModeSetting.Click += (_, _) => Toggle(() => LibrarySettings.GameMode = !LibrarySettings.GameMode);
        view.AppsToCloseGroup.Click += (_, _) => OpenAppsToClose();
        view.ReopenSetting.Click += (_, _) => Toggle(() => LibrarySettings.ReopenApps = !LibrarySettings.ReopenApps);
        view.AutoTdpSetting.Click += (_, _) => StepAutoTdp(1);
        view.AutoTdpTemperatureSetting.Click += (_, _) => StepAutoTdpTemperature(1);
        view.AutoTdpWattsSetting.Click += (_, _) => StepAutoTdpWatts(1);
        view.FanCurveSetting.Click += (_, _) => StepFanCurve(1);
        view.StoreClosingSetting.Click += (_, _) => StepStoreClosing(1);
        view.StoreStartupItem.Click += (_, _) => OpenStartup(view.StoreStartupItem);
        view.QuickResumeSetting.Click += (_, _) => ToggleQuickResume();
        view.ResumeSetting.Click += (_, _) => Toggle(() => LibrarySettings.ResumeAfterSleep = !LibrarySettings.ResumeAfterSleep);
        view.WhilePlayingSettingsBackItem.Click += (_, _) => menu.Back();
        view.AddRunningAppItem.Click += (_, _) => OpenRunningApps();
        view.AppsToCloseBackItem.Click += (_, _) => menu.Back();
        view.RunningAppsBackItem.Click += (_, _) => menu.Back();

        // Clean up.
        view.MaintenanceGroup.Click += (_, _) => OpenMaintenance();
        view.TempItem.Click += (_, _) => ConfirmClean(view.TempItem, "Temporary files", Cleanup.ClearTemp);
        view.ShaderItem.Click += (_, _) => ConfirmClean(view.ShaderItem, "Shader caches", () => Cleanup.Clear(Cleanup.ShaderFolders));
        view.RecycleItem.Click += (_, _) => ConfirmClean(view.RecycleItem, "Recycle Bin", () => Cleanup.EmptyRecycleBin()
            ? new CleanResult(1, _measured?.Recycle ?? 0, 0)
            : new CleanResult(0, 0, 0));
        view.StartupGroup.Click += (_, _) => OpenStartup(view.StartupGroup);
        view.MaintenanceSettingsBackItem.Click += (_, _) => menu.Back();
        view.StartupBackItem.Click += (_, _) => menu.Back();

        // Moving off an armed row disarms it, so the second press is always a deliberate one.
        foreach (var row in new[] { view.TempItem, view.ShaderItem, view.RecycleItem })
        {
            row.LostKeyboardFocus += (_, _) =>
            {
                if (ReferenceEquals(_menu.Armed, row))
                {
                    _menu.Armed = null;
                    RefreshMaintenance();
                }
            };
        }

        // Lists inside a group go back to that group.
        menu.SetBack(view.AppsToCloseItems, () =>
        {
            OpenWhilePlaying(view.AppsToCloseGroup);
            return true;
        });
        menu.SetBack(view.RunningAppsItems, () =>
        {
            OpenAppsToClose(view.AddRunningAppItem);
            return true;
        });
        menu.SetBack(view.StartupItems, () =>
        {
            if (ReferenceEquals(_startupFrom, view.StoreStartupItem))
            {
                OpenWhilePlaying(view.StoreStartupItem);
            }
            else
            {
                OpenMaintenance(view.StartupGroup);
            }

            return true;
        });

        menu.AddStepper(Step);
    }

    // ----- Play and sleep -----

    private void OpenWhilePlaying(Button? focus = null)
    {
        RefreshWhilePlaying();
        _menu.Show(_view.WhilePlayingItems, "Play and sleep", SettingsMenu.SleepTweaks, focus);
    }

    private void Toggle(Action change)
    {
        change();
        RefreshWhilePlaying();
    }

    private void RefreshWhilePlaying()
    {
        var view = _view;
        view.QuickResumeSetting.Tag = SettingSteps.OnOff(LibrarySettings.QuickResume);
        view.ResumeSetting.Tag = SettingSteps.OnOff(LibrarySettings.ResumeAfterSleep);

        view.GameModeSetting.Tag = SettingSteps.OnOff(LibrarySettings.GameMode);
        view.AppsToCloseGroup.Tag = BackgroundApps.Choices().Count(c => c.Close) is var chosen and > 0 ? chosen.ToString() : "None";

        view.ReopenSetting.Tag = SettingSteps.OnOff(LibrarySettings.ReopenApps);

        view.StoreClosingSetting.Tag = LibrarySettings.StoreClosing switch
        {
            StoreClosing.OpenedByGame => "If the game opened it",
            StoreClosing.Always => "Always",
            _ => "Off"
        };

        view.AutoTdpSetting.Tag = LibrarySettings.AutoTdpFps is var fps and > 0 ? $"{fps} fps" : "Off";
        view.AutoTdpTemperatureSetting.Tag = $"{LibrarySettings.AutoTdpTemperature} °C";
        view.AutoTdpWattsSetting.Tag = $"{LibrarySettings.AutoTdpMaxWatts} W";
        view.FanCurveSetting.Tag = AutoTdp.FanName(LibrarySettings.FanCurve);
    }

    private void ToggleQuickResume()
    {
        LibrarySettings.QuickResume = !LibrarySettings.QuickResume;

        // Switched off with a game paused: it runs again, behind the library, as it would have.
        if (!LibrarySettings.QuickResume && _host.Session is { } session)
        {
            _host.ResumeGame(session);
        }

        RefreshWhilePlaying();
    }

    /// <summary>Off, then each frame rate. Takes effect from the next game started.</summary>
    private void StepAutoTdp(int step) =>
        Toggle(() => LibrarySettings.AutoTdpFps = SettingSteps.StepAmong(LibrarySettings.AutoTdpFps, [0, .. AutoTdp.FrameRates], step));

    private void StepAutoTdpTemperature(int step) =>
        Toggle(() => LibrarySettings.AutoTdpTemperature = SettingSteps.StepAmong(LibrarySettings.AutoTdpTemperature, AutoTdp.TemperatureLimits, step));

    private void StepAutoTdpWatts(int step) =>
        Toggle(() => LibrarySettings.AutoTdpMaxWatts = SettingSteps.StepAmong(LibrarySettings.AutoTdpMaxWatts, AutoTdp.MostWatts, step));

    /// <summary>The Ally's own, then each of TDP Engine's curves. Takes effect from the next game started.</summary>
    private void StepFanCurve(int step)
    {
        var choices = AutoTdp.FanChoices;
        var index = Math.Max(0, Array.IndexOf(choices, LibrarySettings.FanCurve));
        Toggle(() => LibrarySettings.FanCurve = choices[(index + step + choices.Length) % choices.Length]);
    }

    private void StepStoreClosing(int step) =>
        Toggle(() => LibrarySettings.StoreClosing = SettingSteps.Step(LibrarySettings.StoreClosing, step));

    /// <summary>Left and right on the rows of Play and sleep.</summary>
    private bool Step(Button row, int step)
    {
        var view = _view;
        if (ReferenceEquals(row, view.StoreClosingSetting))
        {
            StepStoreClosing(step);
        }
        else if (ReferenceEquals(row, view.AutoTdpSetting))
        {
            StepAutoTdp(step);
        }
        else if (ReferenceEquals(row, view.AutoTdpTemperatureSetting))
        {
            StepAutoTdpTemperature(step);
        }
        else if (ReferenceEquals(row, view.AutoTdpWattsSetting))
        {
            StepAutoTdpWatts(step);
        }
        else if (ReferenceEquals(row, view.FanCurveSetting))
        {
            StepFanCurve(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    // ----- Programs to close -----

    private void OpenAppsToClose(Button? focus = null)
    {
        _menu.ClearRows(_view.AppsToCloseItems);
        foreach (var (app, close) in BackgroundApps.Choices())
        {
            Button? row = null;
            var on = close;
            row = _menu.AddRow(_view.AppsToCloseItems, _view.AddRunningAppItem, app.Name, AppDetail(on), () =>
            {
                if (BackgroundApps.SetClose(app, !on))
                {
                    on = !on;
                    row!.Tag = AppDetail(on);
                }
            });
        }

        _menu.Show(_view.AppsToCloseItems, "Programs to close", "Only your own programs are listed. Windows is never touched", focus);
    }

    private static string AppDetail(bool close) => close ? "Closed" : "Left running";

    private void OpenRunningApps()
    {
        _menu.ClearRows(_view.RunningAppsItems);
        _menu.Show(_view.RunningAppsItems, "Add a running program", "Looking at what is running...");
        _ = ListRunningAppsAsync();
    }

    private async Task ListRunningAppsAsync()
    {
        var listing = ++_listing;
        var running = await Task.Run(BackgroundApps.Running);
        if (listing != _listing || !ReferenceEquals(_menu.List, _view.RunningAppsItems))
        {
            return;
        }

        _menu.Subtitle = running.Count == 0 ? "No other programs are running" : "These close when a game starts, from the next game on.";
        foreach (var (title, exe) in running)
        {
            _menu.AddRow(_view.RunningAppsItems, _view.RunningAppsBackItem, title, null, () =>
            {
                _notices.Show(BackgroundApps.AddCustom(exe, title) ? $"{title} will be closed while you play." : $"{title} could not be added.");
                OpenAppsToClose();
            });
        }

        _menu.ShowItems();
    }

    // ----- Clean up -----

    private void OpenMaintenance(Button? focus = null)
    {
        var view = _view;
        _menu.Armed = null;
        view.TempItem.Tag = view.ShaderItem.Tag = view.RecycleItem.Tag = "Measuring...";
        view.StartupGroup.Tag = null;
        _menu.Show(view.MaintenanceItems, "Clean up", StorageSummary(), focus);
        _ = MeasureAsync();
    }

    private async Task MeasureAsync()
    {
        var listing = ++_listing;
        var measured = await Task.Run(() => (Cleanup.TempSize(), Cleanup.SizeOf(Cleanup.ShaderFolders), Cleanup.RecycleBinSize()));
        var startup = await Task.Run(StartupApps.Read);
        if (listing != _listing || !ReferenceEquals(_menu.List, _view.MaintenanceItems))
        {
            return;
        }

        _measured = measured;
        var on = startup.Count(a => a.Enabled);
        _view.StartupGroup.Tag = $"{on} on";
        RefreshMaintenance();
    }

    private void RefreshMaintenance()
    {
        const string confirm = "Press again to confirm";
        if (_measured is not { } measured)
        {
            return;
        }

        var view = _view;
        view.TempItem.Tag = ReferenceEquals(_menu.Armed, view.TempItem) ? confirm
            : CleanResult.Size(measured.Temp);
        view.ShaderItem.Tag = ReferenceEquals(_menu.Armed, view.ShaderItem) ? confirm
            : CleanResult.Size(measured.Shaders);
        view.RecycleItem.Tag = ReferenceEquals(_menu.Armed, view.RecycleItem) ? confirm
            : measured.Recycle is { } bin ? CleanResult.Size(bin) : "Unknown";
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

    /// <summary>Arms a clean-up row on its first press and runs it on the second.</summary>
    private async void ConfirmClean(Button row, string name, Func<CleanResult> clean)
    {
        if (!ReferenceEquals(_menu.Armed, row))
        {
            _menu.Armed = row;
            RefreshMaintenance();
            return;
        }

        _menu.Armed = null;
        row.Tag = "Clearing...";
        var result = await Task.Run(clean);
        Program.Log($"{name}: {result.Describe()}");
        _notices.Show($"{name}: {result.Describe()}.");

        if (ReferenceEquals(_menu.List, _view.MaintenanceItems))
        {
            _menu.Subtitle = StorageSummary();
            _ = MeasureAsync();
        }
    }

    // ----- Startup apps -----

    private void OpenStartup(Button from)
    {
        _startupFrom = from;
        _menu.ClearRows(_view.StartupItems);
        var apps = StartupApps.Read();
        foreach (var app in apps)
        {
            var current = app;
            Button? row = null;
            row = _menu.AddRow(_view.StartupItems, _view.StartupBackItem, app.Name, SettingSteps.OnOff(app.Enabled), () =>
            {
                if (StartupApps.SetEnabled(current, !current.Enabled))
                {
                    current = current with { Enabled = !current.Enabled };
                    row!.Tag = SettingSteps.OnOff(current.Enabled);
                }
            });

            // Everyone's entries need administrator rights, which the main app has.
            row.IsEnabled = !app.ForAllUsers;
        }

        // The store apps are first in the list, so that is where the focus starts.
        _menu.Show(_view.StartupItems, "Startup apps",
            apps.Count == 0 ? "Nothing starts with Windows" : "Takes effect the next time you sign in");
    }
}
