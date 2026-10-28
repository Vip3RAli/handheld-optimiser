using System;
using System.Linq;
using System.Threading.Tasks;
using HandheldOptimiser.PowerService;
using Microsoft.Gaming.XboxGameBar;
using Windows.Storage;
using Windows.System.Power;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Navigation;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// TDP Engine in Game Bar: what it is doing, and buttons for the frame rate and fan curve. A change goes
    /// to whatever the library started for the game; with nothing running, it starts TDP Engine for the
    /// busiest program, which stops itself a minute after the game closes.
    /// </summary>
    public sealed partial class TdpWidget : Page
    {
        // The Ally's highest power limit: Armoury Crate's Turbo plugged in, and on battery.
        private const int PluggedInWatts = 30;
        private const int BatteryWatts = 25;

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        // The slider's choice is sent once it has stopped moving for this long, not at every step.
        private readonly DispatcherTimer _sliderPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private bool _loaded;
        private bool _placingSlider;
        private bool _placingBoost;
        private bool _placingScreenStats;
        private bool _placingGpuMemory;
        private bool _placingMode;
        private bool _placingController;
        private bool _placingFrameRate;

        // The frame rate slider's five stops. In Auto the first is None, no target; in Manual it is Off.
        private static readonly int[] FrameRateStops = { 0, 30, 40, 45, 60 };

        // The frame rate is sent once the slider has stopped moving for this long, as the power limit is.
        private readonly DispatcherTimer _frameRatePause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private bool _frameRateSliding;

        // The fan curve and overlay size, each a slider of named stops.
        private readonly StepSlider _fans;
        private readonly StepSlider _overlay;
        // TDP Engine's mode and the last manual choices, from its last answer, kept in the widget's own settings
        // so they are right while TDP Engine is not running, which is most of the time between games.
        private const string ModeSetting = "Mode";
        private const string ManualPowerSetting = "ManualPowerLimit";
        private const string ManualFansSetting = "ManualFans";
        private readonly ApplicationDataContainer _saved = ApplicationData.Current.LocalSettings;
        private string _mode;
        private int _manualPower;
        private string _manualFans;

        // Whether the player just picked a GPU memory size, which asks to restart.
        private bool _askRestart;
        private bool _sliding;
        private EngineStatus _status = EngineStatus.Off;
        private bool _busy;

        public TdpWidget()
        {
            InitializeComponent();
            _mode = _saved.Values[ModeSetting] as string ?? AutoTdpProtocol.AutoModeName;
            _manualPower = _saved.Values[ManualPowerSetting] as int? ?? 0;
            _manualFans = _saved.Values[ManualFansSetting] as string;
            _timer.Tick += (sender, e) => Refresh();
            _sliderPause.Tick += (sender, e) => SendSlider();
            _frameRatePause.Tick += (sender, e) => SendFrameRate();

            // The fan curves from TDP Engine's own Auto, through the quietest, to the loudest.
            _fans = new StepSlider(FanSlider, FanValue,
                new[] { AutoTdpProtocol.DynamicFans, AutoTdpProtocol.QuietFans, "", AutoTdpProtocol.CoolFans, AutoTdpProtocol.FullFans },
                new[] { "Auto", "Quiet", "Default", "Cool", "Max" },
                fans => Change(CurrentFps, CurrentPower, fans));
            _overlay = new StepSlider(OverlaySlider, OverlayValue,
                new[] { AutoTdpProtocol.OverlayOff, AutoTdpProtocol.OverlaySmall, AutoTdpProtocol.OverlayMedium, AutoTdpProtocol.OverlayFull },
                new[] { "Off", "Small", "Medium", "Full" },
                level => Send(() => EngineClient.SetOverlay(level, null, null)));
            Loaded += (sender, e) =>
            {
                _loaded = true;
                PowerManager.PowerSupplyStatusChanged += OnPowerSupply;
                ShowPowerSupply();
                Show(_status);
                UpdatePolling();
            };
            Unloaded += (sender, e) =>
            {
                _loaded = false;
                PowerManager.PowerSupplyStatusChanged -= OnPowerSupply;
                _timer.Stop();
                if (_sliding)
                {
                    _sliderPause.Stop();
                    SendSlider();
                }

                if (_frameRateSliding)
                {
                    SendFrameRate();
                }

                _fans.Flush();
                _overlay.Flush();
            };
        }

        // Game Bar keeps the widget loaded after it closes, so without this it would ask TDP Engine every two
        // seconds for as long as the player is signed in. Null when opened on its own, outside Game Bar.
        private XboxGameBarWidget _widget;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            if (_widget != null)
            {
                _widget.VisibleChanged += OnVisibleChanged;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_widget != null)
            {
                _widget.VisibleChanged -= OnVisibleChanged;
                _widget = null;
            }
        }

        /// <summary>Game Bar may say so on another thread, and the timer belongs to the window's.</summary>
        private async void OnVisibleChanged(XboxGameBarWidget sender, object args) =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePolling);

        /// <summary>
        /// Asks TDP Engine for news only while the widget can be seen: Game Bar is open, or the widget is
        /// pinned over the game. Shown again, it asks at once rather than showing stale readings.
        /// </summary>
        private void UpdatePolling()
        {
            if (_loaded && (_widget == null || _widget.Visible))
            {
                if (!_timer.IsEnabled)
                {
                    Refresh();
                    _timer.Start();
                }
            }
            else
            {
                _timer.Stop();
            }
        }

        private async void Refresh()
        {
            if (_busy || Moving)
            {
                return;
            }

            var status = await Task.Run(() => EngineClient.Status());
            if (!_busy && !Moving)
            {
                Show(status);
            }
        }

        /// <summary>True while the player moves any slider and the choice is not sent yet.</summary>
        private bool Moving => _sliding || _frameRateSliding || _fans.Moving || _overlay.Moving;

        private void OnFrameRateSlider(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded || _placingFrameRate)
            {
                return;
            }

            // Held back until it stops, so a sweep from None to 60 is one change rather than four.
            _frameRateSliding = true;
            FrameRateValue.Text = FrameRateName(SliderFrameRate, running: true);
            _frameRatePause.Stop();
            _frameRatePause.Start();
        }

        private int SliderFrameRate => FrameRateStops[Math.Max(0, Math.Min(FrameRateStops.Length - 1, (int)Math.Round(FrameRateSlider.Value)))];

        // In Auto only the frame rate is sent: TDP Engine picks the rest, and 0 is None, not off. Stopped,
        // the slider rests on None, so None is reached by moving off it and back within the pause.
        private void SendFrameRate()
        {
            _frameRatePause.Stop();
            _frameRateSliding = false;
            var target = SliderFrameRate;
            if (_status.Running && target == CurrentFps)
            {
                Show(_status);
                return;
            }

            if (Auto)
            {
                Change(target, 0, null, start: true);
            }
            else
            {
                Change(target, CurrentPower, CurrentFans);
            }
        }

        /// <summary>What the frame rate stop says: a number, None in Auto, Off in Manual or while TDP Engine is stopped.</summary>
        private string FrameRateName(int fps, bool running) =>
            fps > 0 ? fps + " fps" : running && Auto ? "None" : "Off";

        private void OnMode(object sender, RoutedEventArgs e)
        {
            // Set from TDP Engine's answer, not by the player: nothing to send.
            if (_placingMode)
            {
                return;
            }

            var mode = ModeSwitch.IsOn ? AutoTdpProtocol.AutoModeName : AutoTdpProtocol.ManualMode;
            Send(() => EngineClient.SetMode(mode));
        }

        /// <summary>Auto unless TDP Engine said Manual. Kept from its last answer while it is not running.</summary>
        private bool Auto => _mode != AutoTdpProtocol.ManualMode;

        private void OnPowerLimit(object sender, RoutedEventArgs e)
        {
            _sliderPause.Stop();
            _sliding = false;
            Change(CurrentFps, int.Parse((string)((FrameworkElement)sender).Tag), CurrentFans);
        }

        private void OnPowerSlider(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_loaded || _placingSlider)
            {
                return;
            }

            // Held back until it stops, so a sweep from 7 to 30 W is one change rather than twenty.
            _sliding = true;
            AutoPower.IsChecked = false;
            PowerValue.Text = (int)e.NewValue + " W";
            _sliderPause.Stop();
            _sliderPause.Start();
        }

        private async void OnPowerSupply(object sender, object e) =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ShowPowerSupply);

        /// <summary>The slider's top for the charger as it is now; unplugged above the battery's top, the limit comes down to it.</summary>
        private void ShowPowerSupply()
        {
            var top = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent ? BatteryWatts : PluggedInWatts;
            if ((int)PowerSlider.Maximum == top)
            {
                return;
            }

            // Lowering the top moves the thumb down with it, which is not the player's choice.
            _placingSlider = true;
            PowerSlider.Maximum = top;
            _placingSlider = false;
            PowerMax.Text = top + " W";

            if (CurrentPower > top)
            {
                _sliderPause.Stop();
                _sliding = false;
                Change(CurrentFps, top, CurrentFans);
            }
            else if (_sliding)
            {
                // Mid-move, the thumb may have come down with the top.
                PowerValue.Text = (int)PowerSlider.Value + " W";
            }
        }

        private void SendSlider()
        {
            _sliderPause.Stop();
            _sliding = false;
            Change(CurrentFps, (int)PowerSlider.Value, CurrentFans);
        }

        private void OnControllerMode(object sender, RoutedEventArgs e)
        {
            // Set from TDP Engine's answer, not by the player: nothing to send.
            if (_placingController)
            {
                return;
            }

            var mode = ControllerSwitch.IsOn ? AutoTdpProtocol.GamepadMode : AutoTdpProtocol.DesktopMode;
            Send(() => EngineClient.SetControllerMode(mode));
        }

        private void OnCpuBoost(object sender, RoutedEventArgs e)
        {
            // Set from TDP Engine's answer, not by the player: nothing to send.
            if (_placingBoost)
            {
                return;
            }

            var on = BoostSwitch.IsOn;
            Send(() => EngineClient.SetCpuBoost(on));
        }

        private void OnScreenStats(object sender, RoutedEventArgs e)
        {
            // Set from TDP Engine's answer, not by the player: nothing to send.
            if (_placingScreenStats)
            {
                return;
            }

            var on = ScreenStatsSwitch.IsOn;
            Send(() => EngineClient.SetScreenStats(on));
        }

        private void OnOverlayCorner(object sender, RoutedEventArgs e)
        {
            var corner = (string)((FrameworkElement)sender).Tag;
            Send(() => EngineClient.SetOverlay(null, corner, null));
        }

        private void OnOverlayLayout(object sender, RoutedEventArgs e)
        {
            var layout = (string)((FrameworkElement)sender).Tag;
            Send(() => EngineClient.SetOverlay(null, null, layout));
        }

        /// <summary>
        /// While TDP Engine runs, every answer carries the GPU memory. Stopped, the list shows the sizes from
        /// the last answer, and opening it asks again, starting TDP Engine, so they are current.
        /// </summary>
        private void OnGpuMemoryOpened(object sender, object e)
        {
            if (!_status.Running)
            {
                Send(() => EngineClient.GpuMemory());
            }
        }

        private void OnGpuMemoryPicked(object sender, object e)
        {
            // Closed on the size it already had: nothing to send.
            if (_placingGpuMemory || !(GpuMemorySizes.SelectedItem is ComboBoxItem item) || !(item.Tag is int size)
                || size == (_status.GpuMemoryNext ?? _status.GpuMemoryNow))
            {
                return;
            }

            _askRestart = true;
            Send(() => EngineClient.SetGpuMemory(size));
        }

        /// <summary>Up and down on the closed box move to the next control, as everywhere else in the widget, instead of changing the size.</summary>
        private void OnGpuMemoryKey(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (GpuMemorySizes.IsDropDownOpen)
            {
                return;
            }

            var direction = e.Key == Windows.System.VirtualKey.Up || e.Key == Windows.System.VirtualKey.GamepadDPadUp
                ? Windows.UI.Xaml.Input.FocusNavigationDirection.Up
                : e.Key == Windows.System.VirtualKey.Down || e.Key == Windows.System.VirtualKey.GamepadDPadDown
                    ? Windows.UI.Xaml.Input.FocusNavigationDirection.Down
                    : Windows.UI.Xaml.Input.FocusNavigationDirection.None;

            if (direction != Windows.UI.Xaml.Input.FocusNavigationDirection.None)
            {
                Windows.UI.Xaml.Input.FocusManager.TryMoveFocus(direction);
                e.Handled = true;
            }
        }

        private void OnGpuMemoryRestart(object sender, RoutedEventArgs e)
        {
            ((ToggleButton)sender).IsChecked = false;
            Send(() => EngineClient.Restart());
        }

        private void OnGpuMemoryLater(object sender, RoutedEventArgs e)
        {
            ((ToggleButton)sender).IsChecked = false;
            _askRestart = false;
            GpuMemoryRestart.Visibility = Visibility.Collapsed;
        }

        /// <summary>The GPU memory row: what Windows has, what waits for a restart, and the sizes to pick from.</summary>
        private void ShowGpuMemory(EngineStatus status)
        {
            // An answer that says it cannot be set hides the row. No answer at all leaves it as it was.
            if (!status.Reachable)
            {
                return;
            }

            var offered = status.GpuMemoryOffered;
            GpuMemoryRow.Visibility = offered.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (offered.Length == 0)
            {
                return;
            }

            // The list shows the size from the next restart; while one waits, the line under the name says what Windows has now.
            GpuMemoryNote.Text = status.GpuMemoryNext != null && status.GpuMemoryNow is int now ? SizeName(now) + " until restart" : "";
            GpuMemoryNote.Visibility = GpuMemoryNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _placingGpuMemory = true;
            if (GpuMemorySizes.Items.Count != offered.Length)
            {
                GpuMemorySizes.Items.Clear();
                foreach (var size in offered)
                {
                    GpuMemorySizes.Items.Add(new ComboBoxItem { Content = SizeName(size), Tag = size });
                }
            }

            var shown = status.GpuMemoryNext ?? status.GpuMemoryNow;
            GpuMemorySizes.SelectedItem = GpuMemorySizes.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int size && size == shown);
            _placingGpuMemory = false;

            // Asked only after the player picks a size, and gone once nothing waits.
            var asking = _askRestart && status.GpuMemoryNext != null && status.Error == null;
            GpuMemoryRestart.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
            if (asking)
            {
                GpuMemoryQuestion.Text = "Restart now to use " + SizeName(status.GpuMemoryNext.Value) + "?";
            }
        }

        private static string SizeName(int size) => size == 0 ? "Auto" : size + " GB";

        /// <summary>Sends one change on a background thread, shows the answer, and ignores presses meanwhile.</summary>
        private async void Send(Func<EngineStatus> change)
        {
            if (_busy)
            {
                Show(_status);
                return;
            }

            _busy = true;
            SetState("", "Applying…");
            try
            {
                Show(await Task.Run(change));
            }
            finally
            {
                _busy = false;
            }
        }

        private int CurrentFps => _status.Running ? _status.TargetFps : 0;

        // With nothing running, Manual offers what was chosen in it last.
        private int CurrentPower => _status.Running ? _status.PowerLimit : _manualPower;

        private string CurrentFans => _status.Running ? _status.FanCurve : _manualFans;

        private async void Change(int targetFps, int powerLimit, string fanCurve, bool start = false)
        {
            if (_busy)
            {
                Show(_status);
                return;
            }

            _busy = true;
            SetState("", _status.Running ? "Applying…" : "Starting TDP Engine…");
            try
            {
                Show(await Task.Run(() => EngineClient.Update(targetFps, powerLimit, fanCurve, start)));
            }
            finally
            {
                _busy = false;
            }
        }

        private void Show(EngineStatus status)
        {
            _status = status;
            if (status.Reachable && status.Mode != null)
            {
                _mode = status.Mode;
                _manualPower = status.ManualPowerLimit;
                _manualFans = status.ManualFans;
                _saved.Values[ModeSetting] = _mode;
                _saved.Values[ManualPowerSetting] = _manualPower;
                _saved.Values[ManualFansSetting] = _manualFans;
            }

            // Auto hides what TDP Engine picks itself: the power limit, CPU boost and the fans.
            var manual = Auto ? Visibility.Collapsed : Visibility.Visible;
            _placingMode = true;
            ModeSwitch.IsOn = Auto;
            _placingMode = false;
            ModeNote.Text = Auto ? "TDP Engine sets power, fans and boost" : "You set power, fans and boost";
            PowerLabel.Visibility = manual;
            PowerRow.Visibility = manual;
            BoostRow.Visibility = manual;
            CoolingCard.Visibility = manual;
            FrameRateLowest.Text = Auto ? "None" : "Off";

            ShowFrameRate(status);
            ShowPower(CurrentPower);
            _fans.Show(CurrentFans ?? "");

            // The controller is TDP Engine's only while it replaces Armoury Crate. A switch has no "not known",
            // so while the mode is not known it shows off and the line under it says so.
            ControllerRow.Visibility = status.Replacing ? Visibility.Visible : Visibility.Collapsed;
            _placingController = true;
            ControllerSwitch.IsOn = status.ControllerMode == AutoTdpProtocol.GamepadMode;
            _placingController = false;
            ControllerNote.Text = status.ControllerMode == AutoTdpProtocol.GamepadMode ? "On: the controller plays games"
                : status.ControllerMode == AutoTdpProtocol.DesktopMode ? "Off: the controller works the mouse"
                : "Not known yet";
            ShowBoost(status.CpuBoost);
            _overlay.Show(status.OverlayLevel ?? AutoTdpProtocol.OverlayOff);
            Check(OverlayCorners, status.OverlayCorner ?? "");
            Check(OverlayLayouts, status.OverlayLayout ?? "");
            _placingScreenStats = true;
            ScreenStatsSwitch.IsOn = status.ScreenStats;
            _placingScreenStats = false;
            ShowGpuMemory(status);

            // The readouts only mean something while the engine runs, so the state line stands alone otherwise.
            var readouts = status.Running && status.Error == null;
            Readouts.Visibility = readouts ? Visibility.Visible : Visibility.Collapsed;

            // A small gap under the readouts, and none when they are hidden.
            StateLine.Margin = new Thickness(0, readouts ? 4 : 0, 0, 0);

            if (status.Error != null)
            {
                SetState("", status.Error);
                return;
            }

            if (!status.Running)
            {
                SetState("Off", Auto ? ". Move the frame rate slider to start it." : ". Choose a frame rate, power limit or fan setting to start it.");
                return;
            }

            // With fans only, TDP Engine leaves the power limit to the Ally's power mode, which Armoury Crate sets
            // unless TDP Engine is replacing it, so there is no wattage of TDP Engine's to show.
            SetReadout(WattsValue, WattsUnit, "W", status.Watts?.ToString() ?? "Auto", status.Watts != null);
            SetReadout(FpsValue, FpsUnit, "fps", status.Fps is double fps ? Math.Round(fps).ToString() : "?", status.Fps != null);
            SetReadout(TemperatureValue, TemperatureUnit, "°C", status.Temperature?.ToString() ?? "?", status.Temperature != null);

            if (Auto)
            {
                SetState(status.TargetFps > 0 ? "Holding " + status.TargetFps + " fps" : "Most frames for the power",
                    ". " + (status.Watts?.ToString() ?? "?") + " W, boost " + (status.CpuBoost == true ? "on" : "off") + ".");
            }
            else if (status.Watts == null)
            {
                SetState(status.FanCurve == AutoTdpProtocol.DynamicFans ? "Fans controlled automatically" : FanName(status.FanCurve) + " fans",
                    ". Power controlled automatically.");
            }
            else if (status.State == AutoTdpProtocol.FixedState)
            {
                SetState("Fixed power", ". Held at " + status.Watts + " W.");
            }
            else
            {
                Explain(status.State, status.TargetFps);

                // The state line is the only place that says the tile is a ceiling here rather than a fixed limit.
                if (status.PowerLimit > 0)
                {
                    StateDetail.Text += " Using up to " + status.PowerLimit + " W.";
                }
            }
        }

        /// <summary>
        /// The frame rate slider at the target in force, left alone while the player moves it. Stopped, it rests
        /// on the first stop and says Off.
        /// </summary>
        private void ShowFrameRate(EngineStatus status)
        {
            if (_frameRateSliding)
            {
                return;
            }

            var fps = CurrentFps;
            var stop = Array.IndexOf(FrameRateStops, fps);
            if (stop < 0)
            {
                // A target from elsewhere that is not one of the stops: the nearest stop below it.
                stop = FrameRateStops.Count(s => s <= fps) - 1;
            }

            _placingFrameRate = true;
            FrameRateSlider.Value = Math.Max(0, stop);
            _placingFrameRate = false;
            FrameRateValue.Text = FrameRateName(fps, status.Running);
        }

        /// <summary>Auto lit, or the slider at the limit in force. The slider stays where it was under Auto.</summary>
        private void ShowPower(int powerLimit)
        {
            if (_sliding)
            {
                return;
            }

            AutoPower.IsChecked = powerLimit == 0;
            PowerValue.Text = powerLimit == 0 ? "Auto" : powerLimit + " W";
            if (powerLimit > 0 && (int)PowerSlider.Value != powerLimit)
            {
                _placingSlider = true;
                PowerSlider.Value = powerLimit;
                _placingSlider = false;
            }
        }

        /// <summary>
        /// The boost switch as TDP Engine reports it. A switch has no "not known", so while TDP Engine is not
        /// running it shows off and the line under it says so; turning it on then starts TDP Engine.
        /// </summary>
        private void ShowBoost(bool? on)
        {
            _placingBoost = true;
            BoostSwitch.IsOn = on == true;
            _placingBoost = false;
            BoostNote.Text = on == null ? "Not known while TDP Engine is off" : "Faster bursts, more heat";
        }

        /// <summary>A readout's number, with its unit hidden when the value is a word such as Auto.</summary>
        private static void SetReadout(Run value, Run unit, string unitText, string text, bool known)
        {
            value.Text = text;
            unit.Text = known ? unitText : "";
        }

        /// <summary>The line under the readouts: the state's name in green, then the rest of the sentence.</summary>
        private void SetState(string name, string detail)
        {
            StateName.Text = name;
            StateDetail.Text = detail;
        }

        private static void Check(Panel row, string tag)
        {
            foreach (var button in row.Children.OfType<ToggleButton>())
            {
                button.IsChecked = (string)button.Tag == tag;
            }
        }

        private static string FanName(string fans)
        {
            switch (fans)
            {
                case AutoTdpProtocol.QuietFans: return "Quiet";
                case AutoTdpProtocol.CoolFans: return "Cool";
                case AutoTdpProtocol.FullFans: return "Max";
                case AutoTdpProtocol.DynamicFans: return "Auto";
                default: return "Default";
            }
        }

        /// <summary>The service's state names, from AutoTdpState in AutoTdpProtocol.Names.cs.</summary>
        private void Explain(string state, int targetFps)
        {
            switch (state)
            {
                case nameof(AutoTdpState.Holding): SetState("Holding", ". " + targetFps + " fps at the lowest power it can."); break;
                case nameof(AutoTdpState.Raising): SetState("Raising power", ". Below " + targetFps + " fps."); break;
                case nameof(AutoTdpState.Lowering): SetState("Lowering power", ". Above " + targetFps + " fps, so it can use less."); break;
                case nameof(AutoTdpState.AtMaximum): SetState("At the power limit", ". Cannot reach " + targetFps + " fps. Try lower game settings."); break;
                case nameof(AutoTdpState.NoGain): SetState("No gain from more power", ". It would not raise the frame rate."); break;
                case nameof(AutoTdpState.TooHot): SetState("Too hot", ". At the temperature limit, so power is lowered."); break;
                case nameof(AutoTdpState.Waiting): SetState("Waiting", ". Waiting for the game."); break;
                default: SetState("Checking", ". Watching the result of the change."); break;
            }
        }
    }
}
