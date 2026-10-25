using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Quick settings, the library's take on Armoury Crate's Command Center: volume, screen brightness,
/// Windows' power mode, the resolution and the refresh rate, all of which a standard user may change, and
/// closing the chosen background programs now. Nothing opens it for now: its button in the top bar and the
/// controller's Y button were taken away. The menu itself is kept for when it comes back.
/// </summary>
internal sealed class QuickSettingsMenu
{
    private const int VolumeStep = 5;
    private const int BrightnessStep = 10;

    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;

    // What the rows show. Null when this device has no such control, or before it has been read.
    private (int Percent, bool Muted)? _volume;
    private int? _brightness;
    private bool _brightnessRead;
    private PowerMode? _powerMode;
    private (int Current, IReadOnlyList<int> Available)? _refreshRates;
    private (Resolution Current, IReadOnlyList<Resolution> Available)? _resolutions;

    // The brightness to set next. WMI is slow, so presses that come faster than it are folded into one.
    private int? _brightnessWanted;
    private bool _settingBrightness;

    public QuickSettingsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices)
    {
        _view = view;
        _menu = menu;
        _notices = notices;

        view.VolumeSetting.Click += (_, _) => ToggleMute();
        view.BrightnessSetting.Click += (_, _) => StepBrightness(BrightnessStep, wrap: true);
        view.PowerModeSetting.Click += (_, _) => StepPowerMode(1);
        view.RefreshRateSetting.Click += (_, _) => StepRefreshRate(1);

        // A press steps to the next smaller size, and from the smallest back to the largest.
        view.ResolutionSetting.Click += (_, _) => StepResolution(-1);
        view.CloseAppsNowItem.Click += (_, _) => CloseAppsNow();
        menu.AddStepper(Step);
    }

    /// <summary>Once from the button in the top bar or the controller's Y button, neither of which is there now.</summary>
    public void Open()
    {
        if (!_menu.Open(_view.QuickItems, "Quick settings", "This device"))
        {
            return;
        }

        // Read each time, since any of them can change outside the library.
        _volume = Volume.Read();
        _powerMode = PowerModes.Read();
        _refreshRates = RefreshRates.Read();
        _resolutions = Resolutions.Read();
        _brightness = null;
        _brightnessRead = false;
        Refresh();
        RefreshCloseAppsNow();
        _ = ReadBrightnessAsync();
    }

    private async Task ReadBrightnessAsync()
    {
        _brightness = await Task.Run(Brightness.Read);
        _brightnessRead = true;
        Refresh();
    }

    private void Refresh()
    {
        var view = _view;
        view.VolumeSetting.IsEnabled = _volume is not null;
        view.VolumeSetting.Tag = _volume is not { } volume ? "No speakers"
            : volume.Muted ? "Muted"
            : $"{volume.Percent}%";

        view.BrightnessSetting.IsEnabled = _brightness is not null;
        view.BrightnessSetting.Tag = !_brightnessRead ? "Reading..."
            : _brightness is { } level ? $"{level}%"
            : "Not available";

        view.PowerModeSetting.IsEnabled = _powerMode is not null;
        view.PowerModeSetting.Tag = _powerMode is { } mode ? PowerModes.Name(mode)
            : PowerModes.Available ? "Custom"
            : "Not available";

        view.ResolutionSetting.IsEnabled = _resolutions is { Available.Count: > 1 };
        view.ResolutionSetting.Tag = _resolutions is { } sizes ? sizes.Current.ToString() : "Not available";

        view.RefreshRateSetting.IsEnabled = _refreshRates is { Available.Count: > 1 };
        view.RefreshRateSetting.Tag = _refreshRates is { } rates ? $"{rates.Current} Hz" : "Not available";
    }

    /// <summary>Left and right on a quick settings row.</summary>
    private bool Step(Button row, int step)
    {
        var view = _view;
        if (ReferenceEquals(row, view.VolumeSetting))
        {
            StepVolume(step * VolumeStep);
        }
        else if (ReferenceEquals(row, view.BrightnessSetting))
        {
            StepBrightness(step * BrightnessStep, wrap: false);
        }
        else if (ReferenceEquals(row, view.PowerModeSetting))
        {
            StepPowerMode(step);
        }
        else if (ReferenceEquals(row, view.ResolutionSetting))
        {
            StepResolution(step);
        }
        else if (ReferenceEquals(row, view.RefreshRateSetting))
        {
            StepRefreshRate(step);
        }
        else
        {
            return false;
        }

        return true;
    }

    private void StepVolume(int change)
    {
        if (_volume is not { } volume)
        {
            return;
        }

        var percent = Math.Clamp(volume.Percent + change, 0, 100);
        _volume = Volume.Set(percent) ? (percent, false) : Volume.Read();
        Refresh();
    }

    private void ToggleMute()
    {
        if (_volume is not { } volume)
        {
            return;
        }

        _volume = Volume.SetMuted(!volume.Muted) ? (volume.Percent, !volume.Muted) : Volume.Read();
        Refresh();
    }

    /// <param name="wrap">For A and touch, which only go up: past 100% starts again from the lowest level.</param>
    private void StepBrightness(int change, bool wrap)
    {
        if (_brightness is not { } level)
        {
            return;
        }

        var next = level + change;
        next = wrap && next > 100 ? BrightnessStep : Math.Clamp(next, 0, 100);

        // Never all the way to black from here, where the player could not see to turn it back up.
        _brightness = Math.Max(next, BrightnessStep);
        Refresh();
        SetBrightness(_brightness.Value);
    }

    private async void SetBrightness(int percent)
    {
        _brightnessWanted = percent;
        if (_settingBrightness)
        {
            return;
        }

        _settingBrightness = true;
        try
        {
            while (_brightnessWanted is { } wanted)
            {
                _brightnessWanted = null;
                if (!await Task.Run(() => Brightness.Set(wanted)))
                {
                    _notices.Show("The screen did not accept the brightness change.");
                    break;
                }
            }
        }
        finally
        {
            _settingBrightness = false;
        }
    }

    private void StepPowerMode(int step)
    {
        if (_powerMode is not { } mode)
        {
            return;
        }

        var next = SettingSteps.Step(mode, step);
        if (PowerModes.Set(next))
        {
            _powerMode = next;
        }
        else
        {
            _notices.Show("Windows did not change the power mode.");
            _powerMode = PowerModes.Read();
        }

        Refresh();
    }

    private void StepRefreshRate(int step)
    {
        if (_refreshRates is not { Available.Count: > 1 } rates)
        {
            return;
        }

        var index = rates.Available.ToList().IndexOf(rates.Current);
        var next = rates.Available[(Math.Max(index, 0) + step + rates.Available.Count) % rates.Available.Count];
        if (RefreshRates.Set(next) is { } error)
        {
            _notices.Show(error);
        }

        _refreshRates = RefreshRates.Read();
        Refresh();
    }

    /// <param name="step">Positive for a larger size, negative for a smaller one, going round at either end.</param>
    private void StepResolution(int step)
    {
        if (_resolutions is not { Available.Count: > 1 } sizes)
        {
            return;
        }

        // Largest first, so a larger size is further up the list.
        var index = sizes.Available.ToList().IndexOf(sizes.Current);
        var next = sizes.Available[(Math.Max(index, 0) - step + sizes.Available.Count) % sizes.Available.Count];
        if (Resolutions.Set(next) is { } error)
        {
            _notices.Show(error);
        }

        // The rates on offer can differ at the new size.
        _resolutions = Resolutions.Read();
        _refreshRates = RefreshRates.Read();
        Refresh();
    }

    // ----- Close background programs -----

    private void RefreshCloseAppsNow()
    {
        var count = BackgroundApps.Choices().Count(c => c.Close);
        _view.CloseAppsNowItem.IsEnabled = count > 0;
        _view.CloseAppsNowItem.Tag = count == 0 ? "None" : count.ToString();
    }

    private async void CloseAppsNow()
    {
        _menu.Close();
        _notices.Show("Closing background programs...");
        var closed = await Task.Run(() => BackgroundApps.CloseChosen([]));
        _notices.Show(closed.Count == 0 ? "None of the chosen programs were running."
            : $"Closed {string.Join(", ", closed.Select(c => c.Name))}.");
    }
}
