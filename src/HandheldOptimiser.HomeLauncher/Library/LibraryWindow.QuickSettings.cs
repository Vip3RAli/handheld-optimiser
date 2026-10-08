using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Quick settings, the library's take on Armoury Crate's Command Center: volume, screen brightness,
/// Windows' power mode, the resolution and the refresh rate, all of which a standard user may change.
/// </summary>
public partial class LibraryWindow
{
    private const int VolumeStep = 5;
    private const int BrightnessStep = 10;

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

    /// <summary>From the button in the top bar or the controller's Y button.</summary>
    private void OpenQuickSettings()
    {
        _menuTile = null;
        _menu = QuickItems;
        HideMenuLists();

        MenuTitle.Text = "Quick settings";
        MenuStore.Text = "This device";

        // Read each time, since any of them can change outside the library.
        _volume = Volume.Read();
        _powerMode = PowerModes.Read();
        _refreshRates = RefreshRates.Read();
        _resolutions = Resolutions.Read();
        _brightness = null;
        _brightnessRead = false;
        RefreshQuickSettings();
        RefreshCloseAppsNow();
        _ = ReadBrightnessAsync();

        MenuOverlay.Visibility = Visibility.Visible;
        ShowMenuItems();
    }

    private async Task ReadBrightnessAsync()
    {
        _brightness = await Task.Run(Brightness.Read);
        _brightnessRead = true;
        RefreshQuickSettings();
    }

    private void RefreshQuickSettings()
    {
        VolumeSetting.IsEnabled = _volume is not null;
        VolumeSetting.Tag = _volume is not { } volume ? "No speakers found"
            : volume.Muted ? $"Muted ({volume.Percent}%). A unmutes"
            : $"{volume.Percent}%. A mutes";

        BrightnessSetting.IsEnabled = _brightness is not null;
        BrightnessSetting.Tag = !_brightnessRead ? "Reading..."
            : _brightness is { } level ? $"{level}%"
            : "Not adjustable on this screen";

        PowerModeSetting.IsEnabled = _powerMode is not null;
        PowerModeSetting.Tag = _powerMode is { } mode ? PowerModes.Name(mode)
            : PowerModes.Available ? "Set to a mode Windows does not name"
            : "Only with Windows' Balanced power plan";

        ResolutionSetting.IsEnabled = _resolutions is { Available.Count: > 1 };
        ResolutionSetting.Tag = _resolutions is not { } sizes ? "Not available on this screen"
            : sizes.Available.Count > 0 && sizes.Current != sizes.Available[0] ? $"{sizes.Current}. Lower uses less battery"
            : sizes.Current.ToString();

        RefreshRateSetting.IsEnabled = _refreshRates is { Available.Count: > 1 };
        RefreshRateSetting.Tag = _refreshRates is { } rates ? $"{rates.Current} Hz" : "Not available on this screen";
    }

    /// <summary>Left and right on a quick settings row.</summary>
    private bool StepQuickSetting(Button row, int step)
    {
        if (ReferenceEquals(row, VolumeSetting))
        {
            StepVolume(step * VolumeStep);
        }
        else if (ReferenceEquals(row, BrightnessSetting))
        {
            StepBrightness(step * BrightnessStep, wrap: false);
        }
        else if (ReferenceEquals(row, PowerModeSetting))
        {
            StepPowerMode(step);
        }
        else if (ReferenceEquals(row, ResolutionSetting))
        {
            StepResolution(step);
        }
        else if (ReferenceEquals(row, RefreshRateSetting))
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
        RefreshQuickSettings();
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        if (_volume is not { } volume)
        {
            return;
        }

        _volume = Volume.SetMuted(!volume.Muted) ? (volume.Percent, !volume.Muted) : Volume.Read();
        RefreshQuickSettings();
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
        RefreshQuickSettings();
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
                    StatusText.Text = "The screen did not accept the brightness change.";
                    break;
                }
            }
        }
        finally
        {
            _settingBrightness = false;
        }
    }

    private void OnStepBrightness(object sender, RoutedEventArgs e) => StepBrightness(BrightnessStep, wrap: true);

    private void StepPowerMode(int step)
    {
        if (_powerMode is not { } mode)
        {
            return;
        }

        var next = Step(mode, step);
        if (PowerModes.Set(next))
        {
            _powerMode = next;
        }
        else
        {
            StatusText.Text = "Windows did not change the power mode.";
            _powerMode = PowerModes.Read();
        }

        RefreshQuickSettings();
    }

    private void OnStepPowerMode(object sender, RoutedEventArgs e) => StepPowerMode(1);

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
            StatusText.Text = error;
        }

        _refreshRates = RefreshRates.Read();
        RefreshQuickSettings();
    }

    private void OnStepRefreshRate(object sender, RoutedEventArgs e) => StepRefreshRate(1);

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
            StatusText.Text = error;
        }

        // The rates on offer can differ at the new size.
        _resolutions = Resolutions.Read();
        _refreshRates = RefreshRates.Read();
        RefreshQuickSettings();
    }

    // A press steps to the next smaller size, and from the smallest back to the largest.
    private void OnStepResolution(object sender, RoutedEventArgs e) => StepResolution(-1);

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        StatusText.Text = "Looking for games...";
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        // An explicit refresh also looks for emulators installed meanwhile.
        await ScanAsync(refreshEmulators: true);
        StatusText.Text = _allTiles.Count == 1 ? "1 game in the library." : $"{_allTiles.Count} games in the library.";
    }

    private void OnOpenQuickSettings(object sender, RoutedEventArgs e) => OpenQuickSettings();
}
