using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// Up and down on the D-pad, worked out by the widget itself: the control in the next row, and in that row
    /// the one nearest across. Game Bar's own guess skipped the sliders (Stick LED Brightness, and Image
    /// Sharpening's Strength) however wide they were made, which the player found on the Ally on 2026-11-11.
    /// Left and right are left to Game Bar and the controls, so a slider still moves with them.
    /// </summary>
    internal sealed class ColumnNavigation
    {
        // Rows closer together than this are one row, as the two quadrants side by side are.
        private const double SameRow = 6;

        private readonly FrameworkElement _page;

        /// <param name="page">The panel that holds every row, inside the scroll viewer.</param>
        public ColumnNavigation(FrameworkElement page)
        {
            _page = page;
        }

        /// <summary>
        /// For the scroll viewer's PreviewKeyDown, which comes before any control's own handling, so a slider
        /// never takes up or down as a change of value. A key on anything that is not one of the page's own
        /// controls, such as a list's open drop-down, is left alone.
        /// </summary>
        public void OnKey(object sender, KeyRoutedEventArgs e)
        {
            var direction = Direction(e.Key);
            if (direction == 0 || !(FocusManager.GetFocusedElement() is Control focused))
            {
                return;
            }

            var stops = new List<Control>();
            Collect(_page, stops);
            if (!stops.Contains(focused))
            {
                return;
            }

            // At the top or bottom there is nowhere to go, and the key is still taken so focus stays in the widget.
            e.Handled = true;
            var next = Next(focused, stops, direction);
            if (next != null)
            {
                next.Focus(FocusState.Keyboard);
                next.StartBringIntoView();
            }
        }

        private static int Direction(VirtualKey key)
        {
            switch (key)
            {
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    return 1;
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    return -1;
                default:
                    return 0;
            }
        }

        /// <summary>The control in the nearest row that way, nearest across to the one with focus, or null.</summary>
        private Control Next(Control from, List<Control> stops, int direction)
        {
            var start = Bounds(from);
            var middle = start.Left + start.Width / 2;
            var rows = new List<(Control Stop, double Gap, double Across)>();
            var nearest = double.MaxValue;
            foreach (var stop in stops)
            {
                if (ReferenceEquals(stop, from))
                {
                    continue;
                }

                var box = Bounds(stop);
                var gap = direction > 0 ? box.Top - start.Bottom : start.Top - box.Bottom;

                // A control beside this one, or behind it, is not the next row.
                if (gap < -SameRow)
                {
                    continue;
                }

                var across = Math.Max(0, Math.Max(box.Left - middle, middle - box.Right));
                rows.Add((stop, gap, across));
                nearest = Math.Min(nearest, gap);
            }

            Control best = null;
            var bestAcross = double.MaxValue;
            foreach (var row in rows)
            {
                if (row.Gap <= nearest + SameRow && row.Across < bestAcross)
                {
                    best = row.Stop;
                    bestAcross = row.Across;
                }
            }

            return best;
        }

        private Rect Bounds(FrameworkElement element) =>
            element.TransformToVisual(_page).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        /// <summary>
        /// Every control the D-pad can stop on, in a shown part of the page. A control's own insides, such as a
        /// slider's thumb, are not looked into.
        /// </summary>
        private static void Collect(DependencyObject parent, List<Control> stops)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement element && element.Visibility != Visibility.Visible)
                {
                    continue;
                }

                if (child is Control control && control.IsTabStop)
                {
                    if (control.IsEnabled && control.ActualHeight > 0)
                    {
                        stops.Add(control);
                    }

                    continue;
                }

                Collect(child, stops);
            }
        }
    }
}
