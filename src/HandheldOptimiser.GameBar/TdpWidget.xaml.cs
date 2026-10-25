using System;
using System.Linq;
using System.Threading.Tasks;
using HandheldOptimiser.PowerService;
using Windows.System.Power;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;

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
        private bool _sliding;
        private EngineStatus _status = EngineStatus.Off;
        private bool _busy;

        public TdpWidget()
        {
            InitializeComponent();
            _timer.Tick += (sender, e) => Refresh();
            _sliderPause.Tick += (sender, e) => SendSlider();
            Loaded += (sender, e) =>
            {
                _loaded = true;
                PowerManager.PowerSupplyStatusChanged += OnPowerSupply;
                ShowPowerSupply();
                Show(_status);
                Refresh();
                _timer.Start();
            };
            Unloaded += (sender, e) =>
            {
                PowerManager.PowerSupplyStatusChanged -= OnPowerSupply;
                _timer.Stop();
                if (_sliding)
                {
                    _sliderPause.Stop();
                    SendSlider();
                }
            };
        }

        private async void Refresh()
        {
            if (_busy || _sliding)
            {
                return;
            }

            var status = await Task.Run(() => EngineClient.Status());
            if (!_busy && !_sliding)
            {
                Show(status);
            }
        }

        private void OnFrameRate(object sender, RoutedEventArgs e) =>
            Change(int.Parse((string)((FrameworkElement)sender).Tag), CurrentPower, CurrentFans);

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

        private void OnFans(object sender, RoutedEventArgs e) =>
            Change(CurrentFps, CurrentPower, (string)((FrameworkElement)sender).Tag);

        private void OnControllerMode(object sender, RoutedEventArgs e)
        {
            var mode = (string)((FrameworkElement)sender).Tag;
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

        private void OnOverlayLevel(object sender, RoutedEventArgs e)
        {
            var level = (string)((FrameworkElement)sender).Tag;
            Send(() => EngineClient.SetOverlay(level, null, null));
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

        private int CurrentPower => _status.Running ? _status.PowerLimit : 0;

        private string CurrentFans => _status.Running ? _status.FanCurve : null;

        private async void Change(int targetFps, int powerLimit, string fanCurve)
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
                Show(await Task.Run(() => EngineClient.Update(targetFps, powerLimit, fanCurve)));
            }
            finally
            {
                _busy = false;
            }
        }

        private void Show(EngineStatus status)
        {
            _status = status;
            Check(FrameRates, CurrentFps.ToString());
            ShowPower(CurrentPower);
            Check(Fans, CurrentFans ?? "");

            // The controller is TDP Engine's only while it replaces Armoury Crate. Neither button is lit when
            // the mode is not known, so the player is not told something that may be wrong.
            ControllerCard.Visibility = status.Replacing ? Visibility.Visible : Visibility.Collapsed;
            Check(ControllerModes, status.ControllerMode ?? "");
            ShowBoost(status.CpuBoost);
            Check(OverlayLevels, status.OverlayLevel ?? "");
            Check(OverlayCorners, status.OverlayCorner ?? "");
            Check(OverlayLayouts, status.OverlayLayout ?? "");

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
                SetState("Off", ". Choose a frame rate, power limit or fan setting to start it.");
                return;
            }

            // With fans only, TDP Engine leaves the power limit to the Ally's power mode, which Armoury Crate sets
            // unless TDP Engine is replacing it, so there is no wattage of TDP Engine's to show.
            SetReadout(WattsValue, WattsUnit, "W", status.Watts?.ToString() ?? "Auto", status.Watts != null);
            SetReadout(FpsValue, FpsUnit, "fps", status.Fps is double fps ? Math.Round(fps).ToString() : "?", status.Fps != null);
            SetReadout(TemperatureValue, TemperatureUnit, "°C", status.Temperature?.ToString() ?? "?", status.Temperature != null);

            if (status.Watts == null)
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

        /// <summary>The line under the readouts: the state's name in cyan, then the rest of the sentence.</summary>
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
