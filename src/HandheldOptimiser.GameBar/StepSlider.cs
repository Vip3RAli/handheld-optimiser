using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// A slider whose stops are named choices, such as the fan curves or the overlay sizes, with the chosen name
    /// shown beside its label. The choice is sent once the slider has stopped moving, so passing over a stop on
    /// the way to another changes nothing, and TDP Engine's answers do not move it while the player does.
    /// </summary>
    internal sealed class StepSlider
    {
        private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

        private readonly Slider _slider;
        private readonly TextBlock _value;
        private readonly string[] _tags;
        private readonly string[] _names;
        private readonly Action<string> _send;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = Pause };
        private bool _placing;

        /// <param name="tags">What is sent for each stop, left to right.</param>
        /// <param name="names">What each stop is called on screen.</param>
        /// <param name="send">Sends the chosen stop's tag.</param>
        public StepSlider(Slider slider, TextBlock value, string[] tags, string[] names, Action<string> send)
        {
            _slider = slider;
            _value = value;
            _tags = tags;
            _names = names;
            _send = send;

            _slider.Minimum = 0;
            _slider.Maximum = tags.Length - 1;
            _slider.StepFrequency = 1;
            _slider.SmallChange = 1;
            _slider.LargeChange = 1;
            _slider.TickFrequency = 1;
            _slider.ValueChanged += OnValueChanged;
            _timer.Tick += (sender, e) => SendNow();
        }

        /// <summary>True while the player is moving it and the choice is not sent yet.</summary>
        public bool Moving { get; private set; }

        /// <summary>Puts it on the stop with this tag, or the first when none has it. Left alone while it moves.</summary>
        public void Show(string tag)
        {
            if (Moving)
            {
                return;
            }

            var stop = Math.Max(0, Array.IndexOf(_tags, tag ?? ""));
            _placing = true;
            _slider.Value = stop;
            _placing = false;
            _value.Text = _names[stop];
        }

        /// <summary>Sends a choice still waiting for its pause, as when the widget closes mid-move.</summary>
        public void Flush()
        {
            if (Moving)
            {
                SendNow();
            }
        }

        private int Stop => Math.Max(0, Math.Min(_tags.Length - 1, (int)Math.Round(_slider.Value)));

        private void OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_placing)
            {
                return;
            }

            Moving = true;
            _value.Text = _names[Stop];
            _timer.Stop();
            _timer.Start();
        }

        private void SendNow()
        {
            _timer.Stop();
            Moving = false;
            _send(_tags[Stop]);
        }
    }
}
