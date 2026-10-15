using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

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
            Change(int.Parse((string)((FrameworkElement)sender).Tag), CurrentFans);

        private void OnFans(object sender, RoutedEventArgs e) =>
            Change(CurrentFps, (string)((FrameworkElement)sender).Tag);

        private int CurrentFps => _status.Running ? _status.TargetFps : 0;

        private string CurrentFans => _status.Running ? _status.FanCurve : null;

        private async void Change(int targetFps, string fanCurve)
        {
            if (_busy)
            {
                Show(_status);
                return;
            }

            _busy = true;
            Detail.Text = _status.Running ? "Changing…" : "Starting TDP Engine…";
            try
            {
                Show(await Task.Run(() => EngineClient.Update(targetFps, fanCurve)));
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
            Check(Fans, CurrentFans ?? "");

            if (status.Error != null)
            {
                Headline.Text = "TDP Engine";
                Detail.Text = status.Error;
                return;
            }

            if (!status.Running)
            {
                Headline.Text = "TDP Engine is off";
                Detail.Text = "Pick a frame rate or fan curve to start it for the game you're playing.";
                return;
            }

            var parts = new[]
            {
                status.Watts is int watts ? watts + " W" : null,
                status.Watts != null && status.Fps is double fps ? Math.Round(fps) + " fps" : null,
                status.Watts == null && status.FanCurve != null ? FanName(status.FanCurve) + " fans" : null,
                status.Temperature is int degrees ? degrees + " °C" : null
            };

            Headline.Text = string.Join(" · ", parts.Where(part => part != null));
            Detail.Text = status.Watts == null
                ? "Armoury Crate sets the power limit."
                : Explain(status.State, status.TargetFps);
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
                default: return "Ally";
            }
        }

        /// <summary>The service's state names, from AutoTdpState in HandheldOptimiser.PowerService.</summary>
        private static string Explain(string state, int targetFps)
        {
            switch (state)
            {
                case "Holding": return "Holding " + targetFps + " fps with the least power.";
                case "Raising": return "Short of " + targetFps + " fps, so raising the power.";
                case "Lowering": return "Above " + targetFps + " fps, so lowering the power.";
                case "AtMaximum": return "At its most watts and still short of " + targetFps + " fps.";
                case "NoGain": return "More power doesn't help this game, so it isn't given any.";
                case "TooHot": return "At the temperature limit, so cooling down.";
                case "Waiting": return "Waiting for the game: loading, paused or in a menu.";
                default: return "Settling after a change.";
            }
        }
    }
}
