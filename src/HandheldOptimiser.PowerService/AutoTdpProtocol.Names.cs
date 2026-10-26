// The names and limits the TDP Engine service, the game library and the Game Bar widget must agree on.
// This file is built into all three. The widget is an older UWP project that compiles C# 7.3, so keep
// this file to what that accepts: a block namespace, constants and enums, and nothing else.

namespace HandheldOptimiser.PowerService
{
    internal static partial class AutoTdpProtocol
    {
        public const string ServiceName = "HandheldOptimiserAutoTdp";
        public const string PipeName = "HandheldOptimiser.AutoTdp";

        public const string Start = "start";
        public const string Status = "status";
        public const string Stop = "stop";

        /// <summary>From the Game Bar widget: hold this frame rate and fan curve now, starting if need be.</summary>
        public const string Update = "update";

        /// <summary>From the library: set the Ally controller's mode, named by the request's ControllerMode.</summary>
        public const string SetControls = "set-controls";

        /// <summary>From the widget: turn the processor's boost on or off, named by the request's CpuBoost.</summary>
        public const string SetBoost = "set-boost";

        /// <summary>From the widget: show the performance overlay at OverlayLevel, in OverlayCorner.</summary>
        public const string SetOverlay = "set-overlay";

        /// <summary>From the overlay, each second: the frame rate of ProcessId, and what else it shows.</summary>
        public const string Overlay = "overlay";

        /// <summary>From the widget: show the stats screen on the Ally's own panel while docked, or not, named by ScreenStats.</summary>
        public const string SetScreenStats = "set-screen-stats";

        /// <summary>From the widget or the app: the GPU memory Windows has now, any size waiting for a restart, and the sizes on offer.</summary>
        public const string GpuMemoryRequest = "gpu-memory";

        /// <summary>From the widget or the app: set the GPU memory to the request's GpuMemory, used from the next restart.</summary>
        public const string SetGpuMemory = "set-gpu-memory";

        /// <summary>From the widget, which cannot itself: restart Windows now, to use a new GPU memory size.</summary>
        public const string Restart = "restart";

        /// <summary>From the widget: Auto or Manual, named by the request's Mode, saved and applied at once.</summary>
        public const string SetMode = "set-mode";

        /// <summary>
        /// The modes. In Auto, TDP Engine picks the power limit, fans and boost for every session, and a frame
        /// rate is optional. In Manual, the player picks them.
        /// </summary>
        public const string AutoModeName = "auto";
        public const string ManualMode = "manual";

        /// <summary>The frame rate an Auto session with none runs to, which no game reaches. A status shows it as 0.</summary>
        public const int NoTargetFps = 1000;

        /// <summary>The overlay's levels: how much it shows, or Off.</summary>
        public const string OverlayOff = "off";
        public const string OverlaySmall = "small";
        public const string OverlayMedium = "medium";
        public const string OverlayFull = "full";

        /// <summary>How the overlay's rows are laid out: one under another, or along one strip.</summary>
        public const string OverlayVertical = "vertical";
        public const string OverlayHorizontal = "horizontal";

        /// <summary>The corners of the screen the overlay can sit in.</summary>
        public const string TopLeft = "top-left";
        public const string TopRight = "top-right";
        public const string BottomLeft = "bottom-left";
        public const string BottomRight = "bottom-right";

        /// <summary>The controller mode names a request and the saved setting carry.</summary>
        public const string GamepadMode = "gamepad";
        public const string DesktopMode = "desktop";

        /// <summary>The state a status gives while a fixed power limit is held, beside the AutoTdpState names.</summary>
        public const string FixedState = "Fixed";

        /// <summary>The fan curves on offer, besides the Ally's own, which is null.</summary>
        public const string QuietFans = "quiet";
        public const string CoolFans = "cool";
        public const string FullFans = "full";

        /// <summary>Moves between the fixed curves as the temperature and the power limit change. See DynamicFans.</summary>
        public const string DynamicFans = "dynamic";

        // What a request leaves at 0 gets these, as when the widget starts it without the library's settings.
        public const int DefaultMaxWatts = 25;
        public const int DefaultTemperature = 80;

        // The limits the service holds any request to, whatever the library sends. The Ally's own range in
        // Armoury Crate is 7 to 30 W.
        public const int LowestWatts = 7;
        public const int HighestWatts = 30;
        public const int LowestFps = 20;
        public const int HighestFps = 144;
        public const int LowestTemperature = 60;
        public const int HighestTemperature = 95;

        /// <summary>
        /// The JSON field names of a request and a status, for the widget, which writes and reads them by
        /// hand. The service and the library use the AutoTdpRequest and AutoTdpStatus records, and a test
        /// checks every name here is one of their properties.
        /// </summary>
        public static class Fields
        {
            public const string Command = "Command";
            public const string TargetFps = "TargetFps";
            public const string PowerLimit = "PowerLimit";
            public const string FanCurve = "FanCurve";
            public const string ControllerMode = "ControllerMode";
            public const string Running = "Running";
            public const string Fps = "Fps";
            public const string Watts = "Watts";
            public const string Temperature = "Temperature";
            public const string State = "State";
            public const string Error = "Error";
            public const string Replacing = "Replacing";
            public const string ControllerConnected = "ControllerConnected";
            public const string CpuBoost = "CpuBoost";
            public const string OverlayLevel = "OverlayLevel";
            public const string OverlayCorner = "OverlayCorner";
            public const string OverlayLayout = "OverlayLayout";
            public const string ScreenStats = "ScreenStats";
            public const string GpuMemory = "GpuMemory";
            public const string GpuMemoryNow = "GpuMemoryNow";
            public const string GpuMemoryNext = "GpuMemoryNext";
            public const string GpuMemoryOffered = "GpuMemoryOffered";
            public const string Mode = "Mode";
            public const string ManualPowerLimit = "ManualPowerLimit";
            public const string ManualFans = "ManualFans";
        }
    }

    /// <summary>What TDP Engine is doing, for the library and the widget to show.</summary>
    internal enum AutoTdpState
    {
        /// <summary>Not enough readings yet, or waiting for the last change to take effect.</summary>
        Settling,

        /// <summary>At the target with the least power it found.</summary>
        Holding,

        /// <summary>Below the target, so the power limit goes up.</summary>
        Raising,

        /// <summary>Above the target, so the power limit comes down.</summary>
        Lowering,

        /// <summary>Below the target at the highest power limit allowed.</summary>
        AtMaximum,

        /// <summary>More power stopped raising the frame rate, as in a game held back by its processor or its own frame cap.</summary>
        NoGain,

        /// <summary>At or over the temperature limit, so the power limit comes down.</summary>
        TooHot,

        /// <summary>Hardly any frames: a loading screen, a menu, or the game paused or behind the library.</summary>
        Waiting
    }
}
