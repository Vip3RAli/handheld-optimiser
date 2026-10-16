using System;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private EngineStatus _status = EngineStatus.Off;
        private bool _busy;

        public TdpWidget()
        {
            InitializeComponent();
            _timer.Tick += (sender, e) => Refresh();
            Loaded += (sender, e) =>
            {
                Show(_status);
                Refresh();
                _timer.Start();
            };
            Unloaded += (sender, e) => _timer.Stop();
        }

        private async void Refresh()
        {
            if (_busy)
            {
                return;
            }

            var status = await Task.Run(() => EngineClient.Status());
            if (!_busy)
            {
                Show(status);
            }
        }

        private void OnFrameRate(object sender, RoutedEventArgs e) =>
            Change(int.Parse((string)((FrameworkElement)sender).Tag), CurrentPower, CurrentFans);

        private void OnPowerLimit(object sender, RoutedEventArgs e) =>
            Change(CurrentFps, int.Parse((string)((FrameworkElement)sender).Tag), CurrentFans);

        private void OnFans(object sender, RoutedEventArgs e) =>
            Change(CurrentFps, CurrentPower, (string)((FrameworkElement)sender).Tag);

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
            SetState("", _status.Running ? "Changing…" : "Starting TDP Engine…");
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
            Check(PowerLimits, CurrentPower.ToString());
            Check(Fans, CurrentFans ?? "");

            // The readouts only mean something while the engine runs, so the state line stands alone otherwise.
            var readouts = status.Running && status.Error == null;
            Readouts.Visibility = readouts ? Visibility.Visible : Visibility.Collapsed;

            // The divider only separates the line from the readouts, so it goes with them.
            StateLine.BorderThickness = new Thickness(0, readouts ? 1 : 0, 0, 0);
            StateLine.Margin = new Thickness(0, readouts ? 10 : 0, 0, 0);
            StateLine.Padding = new Thickness(0, readouts ? 10 : 0, 0, 0);

            if (status.Error != null)
            {
                SetState("", status.Error);
                return;
            }

            if (!status.Running)
            {
                SetState("Off", ". Pick a frame rate, power limit or fan curve to start it for the game you're playing.");
                return;
            }

            // With fans only, Armoury Crate keeps the power limit, so there is no wattage of TDP Engine's to show.
            SetReadout(WattsValue, WattsUnit, "W", status.Watts?.ToString() ?? "Auto", status.Watts != null);
            SetReadout(FpsValue, FpsUnit, "fps", status.Fps is double fps ? Math.Round(fps).ToString() : "?", status.Fps != null);
            SetReadout(TemperatureValue, TemperatureUnit, "°C", status.Temperature?.ToString() ?? "?", status.Temperature != null);

            if (status.Watts == null)
            {
                SetState(FanName(status.FanCurve) + " fans", ". Armoury Crate sets the power limit.");
            }
            else if (status.State == "Fixed")
            {
                SetState("Fixed", " at " + status.Watts + " W.");
            }
            else
            {
                Explain(status.State, status.TargetFps);

                // The state line is the only place that says the tile is a ceiling here rather than a fixed limit.
                if (status.PowerLimit > 0)
                {
                    StateDetail.Text += " At most " + status.PowerLimit + " W.";
                }
            }
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
                case "quiet": return "Quiet";
                case "cool": return "Cool";
                case "full": return "Full speed";
                case "dynamic": return "Dynamic";
                default: return "Ally";
            }
        }

        /// <summary>The service's state names, from AutoTdpState in HandheldOptimiser.PowerService.</summary>
        private void Explain(string state, int targetFps)
        {
            switch (state)
            {
                case "Holding": SetState("Holding", " " + targetFps + " fps with the least power."); break;
                case "Raising": SetState("Raising", " the power: short of " + targetFps + " fps."); break;
                case "Lowering": SetState("Lowering", " the power: above " + targetFps + " fps."); break;
                case "AtMaximum": SetState("At its most watts", " and still short of " + targetFps + " fps."); break;
                case "NoGain": SetState("No gain", ": more power doesn't help this game, so it isn't given any."); break;
                case "TooHot": SetState("Too hot", ": at the temperature limit, so cooling down."); break;
                case "Waiting": SetState("Waiting", " for the game: loading, paused or in a menu."); break;
                default: SetState("Settling", " after a change."); break;
            }
        }
    }
}
