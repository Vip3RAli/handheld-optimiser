using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HandheldOptimiser.PowerService;
using Windows.Data.Json;

namespace HandheldOptimiser.GameBar
{
    /// <summary>What the TDP Engine service said, or why it could not be asked.</summary>
    internal sealed class EngineStatus
    {
        /// <summary>False when the service is not running, so there is nothing to show but off.</summary>
        public bool Reachable;

        public bool Running;
        public double? Fps;
        public int? Watts;
        public int? Temperature;
        public string State;
        public string Error;
        public int TargetFps;
        public string FanCurve;

        /// <summary>The power limit chosen in the widget, or 0 for Auto.</summary>
        public int PowerLimit;

        /// <summary>True while TDP Engine stands in for Armoury Crate on the Ally's controller.</summary>
        public bool Replacing;

        /// <summary>Whether the Ally's controller is found and awake, while replacing.</summary>
        public bool ControllerConnected;

        /// <summary>"gamepad" or "desktop", the mode the controller was last set to, or null when not known.</summary>
        public string ControllerMode;

        /// <summary>Whether the processor's boost is on for the power source in use, or null when not known.</summary>
        public bool? CpuBoost;

        /// <summary>Whether Radeon Super Resolution is on, or null where it is not offered or not read yet.</summary>
        public bool? Rsr;

        /// <summary>Whether AMD Fluid Motion Frames is on, or null where it is not offered or not read yet.</summary>
        public bool? Afmf;

        /// <summary>
        /// The other Radeon settings, each null where it is not offered or not read yet. A range is lowest,
        /// highest and step, or null.
        /// </summary>
        public int? RsrSharpness;
        public int[] RsrSharpnessRange;
        public bool? AntiLag;
        public bool? Chill;
        public int? ChillMin;
        public int? ChillMax;
        public int[] ChillRange;
        public bool? Sharpening;
        public int? SharpeningStrength;
        public int[] SharpeningRange;

        /// <summary>The overlay's level, corner and layout, or null when not known.</summary>
        public string OverlayLevel;
        public string OverlayCorner;
        public string OverlayLayout;

        /// <summary>Whether the stats screen on the Ally's own panel is switched on.</summary>
        public bool ScreenStats;

        /// <summary>"auto" or "manual", or null when TDP Engine did not say, as when it is not running.</summary>
        public string Mode;

        /// <summary>The power limit and fans last chosen in Manual, offered again with no session running.</summary>
        public int ManualPowerLimit;
        public string ManualFans;

        /// <summary>The GPU memory Windows has now, in GB with 0 for Auto, or null when not known.</summary>
        public int? GpuMemoryNow;

        /// <summary>The GPU memory size waiting for a restart, or null when nothing waits.</summary>
        public int? GpuMemoryNext;

        /// <summary>The GPU memory sizes that can be set, empty when this device cannot set it or the service did not say.</summary>
        public int[] GpuMemoryOffered = new int[0];

        public static readonly EngineStatus Off = new EngineStatus();

        public static EngineStatus Failed(string error) => new EngineStatus { Error = error };
    }

    /// <summary>
    /// Talks to the TDP Engine service over its pipe, as the game library does: one line of JSON each way.
    /// The names come from AutoTdpProtocol.Names.cs, shared with the service. Its JSON records are not,
    /// as this project cannot build them, so the JSON is written and read by hand here. Every call
    /// blocks, so call from a background thread.
    /// </summary>
    internal static class EngineClient
    {
        private const string PipePath = @"\\.\pipe\" + AutoTdpProtocol.PipeName;

        public static EngineStatus Status()
        {
            var answer = Send(new JsonObject { [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.Status) }.Stringify());
            return answer == null ? EngineStatus.Off : Read(answer);
        }

        /// <summary>
        /// Holds a frame rate (0 for none), a power limit (0 for Auto: with a frame rate it tops TDP Engine's
        /// range, without one it is held as it is) and a fan curve (null for the Ally's own), starting the
        /// service if need be.
        /// </summary>
        /// <param name="start">Start the service even for all zeros: in Auto that is None, a frame rate of its own, not off.</param>
        public static EngineStatus Update(int targetFps, int powerLimit, string fanCurve, bool start = false)
        {
            var request = new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.Update),
                [AutoTdpProtocol.Fields.TargetFps] = JsonValue.CreateNumberValue(targetFps),
                [AutoTdpProtocol.Fields.PowerLimit] = JsonValue.CreateNumberValue(powerLimit),
                [AutoTdpProtocol.Fields.FanCurve] = string.IsNullOrEmpty(fanCurve) ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(fanCurve)
            }.Stringify();

            var answer = Send(request);
            if (answer == null)
            {
                // Turning off what is not running needs nothing.
                if (!start && targetFps == 0 && powerLimit == 0 && string.IsNullOrEmpty(fanCurve))
                {
                    return EngineStatus.Off;
                }

                var problem = StartService();
                if (problem != null)
                {
                    return EngineStatus.Failed(problem);
                }

                // It opens its pipe a moment after starting.
                for (var tries = 0; answer == null && tries < 30; tries++)
                {
                    Thread.Sleep(200);
                    answer = Send(request);
                }
            }

            return answer == null ? EngineStatus.Failed("TDP Engine is not responding. Try again in a moment.") : Read(answer);
        }

        /// <summary>
        /// Turns the processor's boost on or off, starting the service if need be. Windows keeps the setting
        /// in the power plan, so it holds after the service ends.
        /// </summary>
        /// <summary>A true or false field, or null when it is missing or null, as from an older service.</summary>
        private static bool? Flag(JsonObject json, string name) =>
            json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.Boolean ? value.GetBoolean() : (bool?)null;

        /// <summary>Switches a Radeon setting (one of RadeonState's switch names, such as "rsr") on or off, starting the service if need be.</summary>
        public static EngineStatus SetRadeon(string feature, bool on) =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetRadeon),
                [AutoTdpProtocol.Fields.RadeonFeature] = JsonValue.CreateStringValue(feature),
                [AutoTdpProtocol.Fields.RadeonOn] = JsonValue.CreateBooleanValue(on)
            }.Stringify());

        /// <summary>Sets a Radeon setting's number (one of RadeonState's number names, such as "chill-min"), starting the service if need be.</summary>
        public static EngineStatus SetRadeonValue(string feature, int value) =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetRadeon),
                [AutoTdpProtocol.Fields.RadeonFeature] = JsonValue.CreateStringValue(feature),
                [AutoTdpProtocol.Fields.RadeonValue] = JsonValue.CreateNumberValue(value)
            }.Stringify());

        public static EngineStatus SetCpuBoost(bool on)
        {
            var request = new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetBoost),
                [AutoTdpProtocol.Fields.CpuBoost] = JsonValue.CreateBooleanValue(on)
            }.Stringify();

            return SendStarting(request);
        }

        /// <summary>
        /// Sets the performance overlay's level ("off" to close it), its corner or its layout, any null to
        /// keep it, starting the service if need be. The service starts the overlay and keeps it running.
        /// </summary>
        public static EngineStatus SetOverlay(string level, string corner, string layout)
        {
            var request = new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetOverlay),
                [AutoTdpProtocol.Fields.OverlayLevel] = TextOrNull(level),
                [AutoTdpProtocol.Fields.OverlayCorner] = TextOrNull(corner),
                [AutoTdpProtocol.Fields.OverlayLayout] = TextOrNull(layout)
            }.Stringify();

            return SendStarting(request);
        }

        /// <summary>
        /// Switches the stats screen on the Ally's own panel on or off, starting the service if need be. The
        /// service starts it and keeps it running; it shows only while a monitor is in use.
        /// </summary>
        public static EngineStatus SetMode(string mode) =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetMode),
                [AutoTdpProtocol.Fields.Mode] = JsonValue.CreateStringValue(mode)
            }.Stringify());

        public static EngineStatus SetScreenStats(bool on)
        {
            var request = new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetScreenStats),
                [AutoTdpProtocol.Fields.ScreenStats] = JsonValue.CreateBooleanValue(on)
            }.Stringify();

            return SendStarting(request);
        }

        /// <summary>The GPU memory now, any size waiting for a restart, and the sizes on offer, starting the service if need be.</summary>
        public static EngineStatus GpuMemory() =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.GpuMemoryRequest)
            }.Stringify());

        /// <summary>Sets the GPU memory, in GB with 0 for Auto, used from the next restart.</summary>
        public static EngineStatus SetGpuMemory(int size) =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetGpuMemory),
                [AutoTdpProtocol.Fields.GpuMemory] = JsonValue.CreateNumberValue(size)
            }.Stringify());

        /// <summary>
        /// Asks TDP Engine to restart Windows now, as Game Bar cannot. Starting it if need be: it ends itself a
        /// minute after it was last needed, which can pass while the question waits.
        /// </summary>
        public static EngineStatus Restart() =>
            SendStarting(new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.Restart)
            }.Stringify());

        private static JsonValue TextOrNull(string text) =>
            text == null ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(text);

        /// <summary>Sends a request, starting the service first if it is not running.</summary>
        private static EngineStatus SendStarting(string request)
        {
            var answer = Send(request);
            if (answer == null)
            {
                var problem = StartService();
                if (problem != null)
                {
                    return EngineStatus.Failed(problem);
                }

                // It opens its pipe a moment after starting.
                for (var tries = 0; answer == null && tries < 30; tries++)
                {
                    Thread.Sleep(200);
                    answer = Send(request);
                }
            }

            return answer == null ? EngineStatus.Failed("TDP Engine is not responding. Try again in a moment.") : Read(answer);
        }

        /// <summary>
        /// Sets the Ally controller's mode, "gamepad" or "desktop". The service always runs while it replaces
        /// Armoury Crate, which is the only time this is offered, so it is never started here.
        /// </summary>
        public static EngineStatus SetControllerMode(string mode)
        {
            var request = new JsonObject
            {
                [AutoTdpProtocol.Fields.Command] = JsonValue.CreateStringValue(AutoTdpProtocol.SetControls),
                [AutoTdpProtocol.Fields.ControllerMode] = JsonValue.CreateStringValue(mode)
            }.Stringify();

            var answer = Send(request);
            return answer == null ? EngineStatus.Failed("TDP Engine is not responding. Try again in a moment.") : Read(answer);
        }

        private static EngineStatus Read(string answer)
        {
            if (!JsonObject.TryParse(answer, out var json))
            {
                return EngineStatus.Failed("TDP Engine sent an answer the widget could not read. Update Handheld Optimiser.");
            }

            return new EngineStatus
            {
                Reachable = true,
                Running = json.GetNamedBoolean(AutoTdpProtocol.Fields.Running, false),
                Fps = Number(json, AutoTdpProtocol.Fields.Fps),
                Watts = (int?)Number(json, AutoTdpProtocol.Fields.Watts),
                Temperature = (int?)Number(json, AutoTdpProtocol.Fields.Temperature),
                State = Text(json, AutoTdpProtocol.Fields.State),
                Error = Text(json, AutoTdpProtocol.Fields.Error),
                TargetFps = (int)(Number(json, AutoTdpProtocol.Fields.TargetFps) ?? 0),
                FanCurve = Text(json, AutoTdpProtocol.Fields.FanCurve),
                PowerLimit = (int)(Number(json, AutoTdpProtocol.Fields.PowerLimit) ?? 0),
                Replacing = json.GetNamedBoolean(AutoTdpProtocol.Fields.Replacing, false),
                ControllerConnected = json.GetNamedBoolean(AutoTdpProtocol.Fields.ControllerConnected, false),
                ControllerMode = Text(json, AutoTdpProtocol.Fields.ControllerMode),
                CpuBoost = Flag(json, AutoTdpProtocol.Fields.CpuBoost),
                Rsr = Flag(json, AutoTdpProtocol.Fields.Rsr),
                Afmf = Flag(json, AutoTdpProtocol.Fields.Afmf),
                RsrSharpness = (int?)Number(json, AutoTdpProtocol.Fields.RsrSharpness),
                RsrSharpnessRange = Range(json, AutoTdpProtocol.Fields.RsrSharpnessRange),
                AntiLag = Flag(json, AutoTdpProtocol.Fields.AntiLag),
                Chill = Flag(json, AutoTdpProtocol.Fields.Chill),
                ChillMin = (int?)Number(json, AutoTdpProtocol.Fields.ChillMin),
                ChillMax = (int?)Number(json, AutoTdpProtocol.Fields.ChillMax),
                ChillRange = Range(json, AutoTdpProtocol.Fields.ChillRange),
                Sharpening = Flag(json, AutoTdpProtocol.Fields.Sharpening),
                SharpeningStrength = (int?)Number(json, AutoTdpProtocol.Fields.SharpeningStrength),
                SharpeningRange = Range(json, AutoTdpProtocol.Fields.SharpeningRange),
                OverlayLevel = Text(json, AutoTdpProtocol.Fields.OverlayLevel),
                OverlayCorner = Text(json, AutoTdpProtocol.Fields.OverlayCorner),
                OverlayLayout = Text(json, AutoTdpProtocol.Fields.OverlayLayout),
                ScreenStats = json.GetNamedBoolean(AutoTdpProtocol.Fields.ScreenStats, false),
                GpuMemoryNow = (int?)Number(json, AutoTdpProtocol.Fields.GpuMemoryNow),
                GpuMemoryNext = (int?)Number(json, AutoTdpProtocol.Fields.GpuMemoryNext),
                GpuMemoryOffered = Numbers(json, AutoTdpProtocol.Fields.GpuMemoryOffered),
                Mode = Text(json, AutoTdpProtocol.Fields.Mode),
                ManualPowerLimit = (int)(Number(json, AutoTdpProtocol.Fields.ManualPowerLimit) ?? 0),
                ManualFans = Text(json, AutoTdpProtocol.Fields.ManualFans)
            };
        }

        private static int[] Numbers(JsonObject json, string name)
        {
            if (!json.TryGetValue(name, out var value) || value.ValueType != JsonValueType.Array)
            {
                return new int[0];
            }

            var list = value.GetArray();
            var numbers = new int[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                numbers[i] = list[i].ValueType == JsonValueType.Number ? (int)list[i].GetNumber() : 0;
            }

            return numbers;
        }

        /// <summary>A range of lowest, highest and step, or null when it is missing or not three numbers.</summary>
        private static int[] Range(JsonObject json, string name)
        {
            var numbers = Numbers(json, name);
            return numbers.Length == 3 ? numbers : null;
        }

        private static double? Number(JsonObject json, string name) =>
            json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.Number ? value.GetNumber() : (double?)null;

        private static string Text(JsonObject json, string name) =>
            json.TryGetValue(name, out var value) && value.ValueType == JsonValueType.String ? value.GetString() : null;

        /// <returns>The answer line, or null when the service is not running.</returns>
        private static string Send(string request)
        {
            var pipe = Open();
            if (pipe == InvalidHandle)
            {
                return null;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(request + "\n");
                if (!WriteFile(pipe, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
                {
                    return null;
                }

                // The answer is one line; the service closes its end after it.
                var answer = new StringBuilder();
                var buffer = new byte[4096];
                while (ReadFile(pipe, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) && read > 0)
                {
                    answer.Append(Encoding.UTF8.GetString(buffer, 0, (int)read));
                    var text = answer.ToString();
                    var end = text.IndexOf('\n');
                    if (end >= 0)
                    {
                        return text.Substring(0, end).TrimEnd('\r');
                    }
                }

                return answer.Length > 0 ? answer.ToString() : null;
            }
            finally
            {
                CloseHandle(pipe);
            }
        }

        private static IntPtr Open()
        {
            // The service takes one request at a time, so a busy pipe is tried again shortly.
            for (var tries = 0; tries < 20; tries++)
            {
                var pipe = CreateFile2(PipePath, GenericRead | GenericWrite, 0, OpenExisting, IntPtr.Zero);
                if (pipe != InvalidHandle)
                {
                    return pipe;
                }

                if (Marshal.GetLastWin32Error() != ErrorPipeBusy)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            return InvalidHandle;
        }

        /// <returns>Why it could not be started, or null.</returns>
        private static string StartService()
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                return "Windows could not start TDP Engine from Game Bar. Start a game from the Handheld Optimiser game library to start it.";
            }

            try
            {
                var service = OpenService(manager, AutoTdpProtocol.ServiceName, ServiceStart);
                if (service == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                        ? "TDP Engine is not installed. Reinstall Handheld Optimiser to add it."
                        : "Windows could not start TDP Engine from Game Bar. Start a game from the Handheld Optimiser game library to start it.";
                }

                try
                {
                    if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorAlreadyRunning)
                    {
                        return "Windows could not start TDP Engine. Restart the handheld and try again.";
                    }

                    return null;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        // .NET Native only binds calls outside the UWP API set when they are marked ExactSpelling. Starting a
        // service is one: Store apps may not, but this widget is sideloaded.
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const int ErrorPipeBusy = 231;
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceStart = 0x0010;
        private const int ErrorAlreadyRunning = 1056;
        private const int ErrorServiceDoesNotExist = 1060;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile2(string fileName, uint access, uint share, uint creation, IntPtr parameters);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, byte[] buffer, uint count, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, byte[] buffer, uint count, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
