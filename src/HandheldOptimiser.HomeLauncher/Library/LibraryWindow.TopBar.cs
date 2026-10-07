using System.Windows;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>The top bar: the clock, battery and Wi-Fi readings, and the update banner.</summary>
public partial class LibraryWindow
{
    // Inside width of the battery outline, which the fill is a share of.
    private const double BatteryFillWidth = 22;
    private const int LowBatteryPercent = 20;

    private static readonly Brush NormalBattery = FrozenBrush("#F2F3F5");
    private static readonly Brush LowBattery = FrozenBrush("#E5534B");

    // The library stays open for days, so it looks for a newer release again now and then.
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    // The newer release a check found, and the one whose banner the player closed.
    private Version? _update;
    private Version? _dismissedUpdate;

    private async Task CheckForUpdateAsync()
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckEvery)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;
        if (await Updates.CheckAsync() is { } newer)
        {
            _update = newer;
            UpdateText.Text = $"Handheld Optimiser {Updates.Display(newer)} is available";
            UpdateBanner.Visibility = newer == _dismissedUpdate ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>From the banner or the settings row. The main app does the installing, and reopens when done.</summary>
    private void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        StatusText.Text = Updates.StartUpdate() ?? "Opening Handheld Optimiser to install the update...";
    }

    /// <summary>Hides the banner for this release. The update stays in Settings.</summary>
    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        _dismissedUpdate = _update;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateTopBar()
    {
        ClockText.Text = DateTime.Now.ToString("t");
        _ = UpdateStatusAsync();
    }

    private async Task UpdateStatusAsync()
    {
        if (!_showStatus)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
            WifiBars.Visibility = Visibility.Collapsed;
            return;
        }

        // Off the UI thread: the Wi-Fi reading is a call into the WLAN service.
        var status = await Task.Run(SystemStatus.Read);
        if (!_showStatus)
        {
            // Switched off while the reading was on its way.
            return;
        }

        BatteryPanel.Visibility = status.BatteryPercent is null ? Visibility.Collapsed : Visibility.Visible;
        if (status.BatteryPercent is { } percent)
        {
            BatteryText.Text = $"{percent}%";
            BatteryFill.Width = BatteryFillWidth * percent / 100;
            BatteryFill.Fill = percent <= LowBatteryPercent && !status.PluggedIn ? LowBattery : NormalBattery;
            ChargingBolt.Visibility = status.PluggedIn ? Visibility.Visible : Visibility.Collapsed;
        }

        WifiBars.Visibility = status.WifiQuality is null ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < WifiBars.Children.Count; i++)
        {
            WifiBars.Children[i].Opacity = i < status.WifiBars ? 0.85 : 0.25;
        }
    }

    private static Brush FrozenBrush(string colour)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brush;
    }
}
