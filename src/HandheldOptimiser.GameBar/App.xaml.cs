using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// Game Bar starts the app through its widget activation, once for each time the widget opens. Each
    /// opening gets its own window, which must keep its XboxGameBarWidget for as long as it is open.
    /// </summary>
    sealed partial class App : Application
    {
        private XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args.Kind == ActivationKind.Protocol
                && ((IProtocolActivatedEventArgs)args).Uri.Scheme.Equals("ms-gamebarwidget")
                ? args as XboxGameBarWidgetActivatedEventArgs
                : null;

            // A repeat activation for a widget already open needs nothing.
            if (widgetArgs == null || !widgetArgs.IsLaunchActivation)
            {
                return;
            }

            var frame = new Frame();
            frame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = frame;

            _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);

            // The page is handed the widget so it can stop asking for news while it cannot be seen.
            frame.Navigate(typeof(TdpWidget), _widget);

            Window.Current.Closed += OnWidgetClosed;
            Window.Current.Activate();
        }

        /// <summary>Opened on its own, outside Game Bar, it shows the same panel. Handy for trying it.</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                frame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = frame;
            }

            if (!e.PrelaunchActivated)
            {
                if (frame.Content == null)
                {
                    frame.Navigate(typeof(TdpWidget));
                }

                Window.Current.Activate();
            }
        }

        private void OnWidgetClosed(object sender, CoreWindowEventArgs e)
        {
            _widget = null;
            Window.Current.Closed -= OnWidgetClosed;
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e) =>
            throw new Exception("Failed to load page " + e.SourcePageType.FullName);

        /// <summary>Game Bar never suspends an app with a widget open, so by now the widget is gone.</summary>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            _widget = null;
            deferral.Complete();
        }
    }
}
