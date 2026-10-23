using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The top bar: its buttons on the d-pad, the clock, the battery and Wi-Fi readings, and the banner for a
/// newer release. The tabs are drawn by the window, which knows the games, and the search by
/// <see cref="SearchPresenter"/>.
/// </summary>
internal sealed class TopBarPresenter
{
    // Inside width of the battery outline, which the fill is a share of.
    private const double BatteryFillWidth = 22;
    private const int LowBatteryPercent = 20;

    private static readonly Brush NormalBattery = LibraryBrushes.Frozen("#F2F3F5");
    private static readonly Brush LowBattery = LibraryBrushes.Frozen("#E5534B");

    // The library stays open for days, so it looks for a newer release again now and then.
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(24);

    private readonly TopBarView _view;
    private readonly Border _updateBanner;
    private readonly TextBlock _updateText;
    private readonly TextBlock _acceptHint;
    private readonly LibraryOptions _options;
    private readonly NoticePresenter _notices;
    private readonly Func<bool> _isBrowsing;
    private readonly Func<bool> _isSearchOpen;
    private readonly Action _closeMenu;
    private readonly Action<bool> _restoreFocus;

    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private Version? _dismissedUpdate;

    // The button the d-pad was last on, which Up goes back to.
    private Button? _lastButton;

    /// <param name="isBrowsing">Whether the Old School layout's Not installed card is open, which has the search and nothing else.</param>
    /// <param name="isSearchOpen">Whether the search bar and its store button are showing.</param>
    /// <param name="restoreFocus">Back to the games, as the top bar is left: true is the top bar asking.</param>
    public TopBarPresenter(TopBarView view, Border updateBanner, TextBlock updateText, Button updateNow, Button dismissUpdate,
        TextBlock acceptHint, LibraryOptions options, NoticePresenter notices, Func<bool> isBrowsing, Func<bool> isSearchOpen,
        Action closeMenu, Action<bool> restoreFocus)
    {
        _view = view;
        _updateBanner = updateBanner;
        _updateText = updateText;
        _acceptHint = acceptHint;
        _options = options;
        _notices = notices;
        _isBrowsing = isBrowsing;
        _isSearchOpen = isSearchOpen;
        _closeMenu = closeMenu;
        _restoreFocus = restoreFocus;

        foreach (var button in new[] { view.StoreButton, view.SearchBar, view.SearchButton, view.SettingsButton, view.PowerButton })
        {
            button.GotKeyboardFocus += OnButtonFocused;
        }

        view.SettingsButton.Click += (_, _) => SettingsPressed?.Invoke();
        view.PowerButton.Click += (_, _) => PowerPressed?.Invoke();
        updateNow.Click += (_, _) => UpdateNow();
        dismissUpdate.Click += (_, _) =>
        {
            _dismissedUpdate = Update;
            _updateBanner.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>The gear: the library's settings.</summary>
    public event Action? SettingsPressed;

    /// <summary>The power button: sleep, hibernate, restart and shut down.</summary>
    public event Action? PowerPressed;

    /// <summary>The newer release a check found, or null.</summary>
    public Version? Update { get; private set; }

    public async Task CheckForUpdateAsync()
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckEvery)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;
        if (await Updates.CheckAsync() is { } newer)
        {
            Update = newer;
            _updateText.Text = $"Handheld Optimiser {Updates.Display(newer)} is available";
            _updateBanner.Visibility = newer == _dismissedUpdate ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>From the banner, the settings row or its card. The main app does the installing, and reopens when done.</summary>
    public void UpdateNow()
    {
        _closeMenu();
        _notices.Show(Updates.StartUpdate() ?? "Opening Handheld Optimiser to install the update...");
    }

    // ----- Search, Settings and Power on the d-pad -----

    /// <summary>
    /// The buttons the d-pad goes along, left to right. The search button is only there on the Not
    /// installed tab, and the store button and the search bar while the search is open. The Old School
    /// layout's open card has the search and nothing else.
    /// </summary>
    public Button[] Buttons()
    {
        var view = _view;
        return view.SearchButton.Visibility != Visibility.Visible ? [view.SettingsButton, view.PowerButton]
            : _isBrowsing() ? _isSearchOpen() ? [view.StoreButton, view.SearchBar, view.SearchButton] : [view.SearchButton]
            : _isSearchOpen() ? [view.StoreButton, view.SearchBar, view.SearchButton, view.SettingsButton, view.PowerButton]
            : [view.SearchButton, view.SettingsButton, view.PowerButton];
    }

    public bool IsButton(object? element) => element is Button button && Array.IndexOf(Buttons(), button) >= 0;

    /// <summary>
    /// Up from the top row of games, or from an empty library: the top bar's buttons. On the Not installed
    /// tab that is the search button, or the search bar while the search is open, which is what the top bar
    /// is most wanted for there.
    /// </summary>
    public void Focus()
    {
        var view = _view;
        (view.SearchButton.Visibility != Visibility.Visible ? _lastButton is { IsVisible: true } last ? last : view.SettingsButton
            : _isSearchOpen() ? view.SearchBar
            : view.SearchButton).Focus();
    }

    public void Navigate(Button from, FocusNavigationDirection direction)
    {
        var buttons = Buttons();
        var index = Array.IndexOf(buttons, from);
        switch (direction)
        {
            case FocusNavigationDirection.Left when index > 0:
                buttons[index - 1].Focus();
                break;
            case FocusNavigationDirection.Right when index + 1 < buttons.Length:
                buttons[index + 1].Focus();
                break;
            case FocusNavigationDirection.Down:
                _restoreFocus(true);
                break;
        }
    }

    private void OnButtonFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button button)
        {
            _lastButton = button;
            _acceptHint.Text = ReferenceEquals(button, _view.StoreButton) ? "Next store"
                : ReferenceEquals(button, _view.SearchBar) ? "Type"
                : "Open";
        }
    }

    // ----- The clock, the battery and Wi-Fi -----

    /// <summary>The clock now, and the battery and Wi-Fi once they have been read.</summary>
    public void Refresh()
    {
        _view.ClockText.Text = DateTime.Now.ToString("t");
        _ = UpdateStatusAsync();
    }

    public async Task UpdateStatusAsync()
    {
        var view = _view;
        if (!_options.ShowStatus)
        {
            view.BatteryPanel.Visibility = Visibility.Collapsed;
            view.WifiBars.Visibility = Visibility.Collapsed;
            return;
        }

        // Off the UI thread: the Wi-Fi reading is a call into the WLAN service.
        var status = await Task.Run(SystemStatus.Read);
        if (!_options.ShowStatus)
        {
            // Switched off while the reading was on its way.
            return;
        }

        view.BatteryPanel.Visibility = status.BatteryPercent is null ? Visibility.Collapsed : Visibility.Visible;
        if (status.BatteryPercent is { } percent)
        {
            view.BatteryText.Text = $"{percent}%";
            view.BatteryFill.Width = BatteryFillWidth * percent / 100;
            view.BatteryFill.Fill = percent <= LowBatteryPercent && !status.PluggedIn ? LowBattery : NormalBattery;
            view.ChargingBolt.Visibility = status.PluggedIn ? Visibility.Visible : Visibility.Collapsed;
        }

        view.WifiBars.Visibility = status.WifiQuality is null ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < view.WifiBars.Children.Count; i++)
        {
            view.WifiBars.Children[i].Opacity = i < status.WifiBars ? 0.85 : 0.25;
        }
    }
}
