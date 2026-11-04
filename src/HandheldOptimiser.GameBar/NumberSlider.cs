using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// A slider for a number whose range comes from TDP Engine's answer, such as a Radeon sharpness or Chill's
    /// frame rates, with the number shown beside its label. As with <see cref="StepSlider"/>, the number is sent
    /// once the slider has stopped moving, and TDP Engine's answers do not move it while the player does.
    /// </summary>
    internal sealed class NumberSlider
    {
        private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

        private readonly Slider _slider;
        private readonly TextBlock _value;
        private readonly string _unit;
        private readonly Action<int> _send;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = Pause };
        private bool _placing;

        /// <param name="unit">Written after the number, such as "%" or " fps".</param>
        /// <param name="send">Sends the chosen number.</param>
        public NumberSlider(Slider slider, TextBlock value, string unit, Action<int> send)
        {
            _slider = slider;
            _value = value;
            _unit = unit;
            _send = send;
            _slider.ValueChanged += OnValueChanged;
            _timer.Tick += (sender, e) => SendNow();
        }

        /// <summary>True while the player is moving it and the number is not sent yet.</summary>
        public bool Moving { get; private set; }

        /// <summary>
        /// Puts it at <paramref name="value"/>, within <paramref name="range"/> (lowest, highest and step) when the
        /// answer has one. Left alone while it moves, and when the number is not known.
        /// </summary>
        public void Show(int? value, int[] range)
        {
            if (Moving || value == null)
            {
                return;
            }

            _placing = true;
            if (range != null && range.Length == 3 && range[0] < range[1])
            {
                // Widened before it is narrowed, so the old range never refuses the new one's ends.
                _slider.Minimum = Math.Min(_slider.Minimum, range[0]);
                _slider.Maximum = Math.Max(_slider.Maximum, range[1]);
                _slider.Minimum = range[0];
                _slider.Maximum = range[1];
                var step = Math.Max(1, range[2]);
                _slider.StepFrequency = step;
                _slider.SmallChange = step;
                _slider.LargeChange = Math.Max(step, (range[1] - range[0]) / 10);
            }

            _slider.Value = value.Value;
            _placing = false;
            _value.Text = value.Value + _unit;
        }

        /// <summary>Sends a number still waiting for its pause, as when the widget closes mid-move.</summary>
        public void Flush()
        {
            if (Moving)
            {
                SendNow();
            }
        }

        private int Number => (int)Math.Round(_slider.Value);

        private void OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_placing)
            {
                return;
            }

            Moving = true;
            _value.Text = Number + _unit;
            _timer.Stop();
            _timer.Start();
        }

        private void SendNow()
        {
            _timer.Stop();
            Moving = false;
            _send(Number);
        }
    }
}
