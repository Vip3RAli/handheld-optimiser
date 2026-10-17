using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

        public static readonly EngineStatus Off = new EngineStatus();

        public static EngineStatus Failed(string error) => new EngineStatus { Error = error };
    }

    /// <summary>
    /// Talks to the TDP Engine service over its pipe, as the game library does: one line of JSON each way.
    /// The service's side is AutoTdpProtocol in HandheldOptimiser.PowerService, which this cannot share as
    /// it is not a UWP library, so the names here must stay in step with it. Every call blocks, so call
    /// from a background thread.
    /// </summary>
    internal static class EngineClient
    {
        private const string PipePath = @"\\.\pipe\HandheldOptimiser.AutoTdp";
        private const string ServiceName = "HandheldOptimiserAutoTdp";

        public static EngineStatus Status()
        {
            var answer = Send("{\"Command\":\"status\"}");
            return answer == null ? EngineStatus.Off : Read(answer);
        }

        /// <summary>
        /// Holds a frame rate (0 for none), a power limit (0 for Auto: with a frame rate it tops TDP Engine's
        /// range, without one it is held as it is) and a fan curve (null for the Ally's own), starting the
        /// service if need be.
        /// </summary>
        public static EngineStatus Update(int targetFps, int powerLimit, string fanCurve)
        {
            var request = new JsonObject
            {
                ["Command"] = JsonValue.CreateStringValue("update"),
                ["TargetFps"] = JsonValue.CreateNumberValue(targetFps),
                ["PowerLimit"] = JsonValue.CreateNumberValue(powerLimit),
                ["FanCurve"] = string.IsNullOrEmpty(fanCurve) ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(fanCurve)
            }.Stringify();

            var answer = Send(request);
            if (answer == null)
            {
                // Turning off what is not running needs nothing.
                if (targetFps == 0 && powerLimit == 0 && string.IsNullOrEmpty(fanCurve))
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

            return answer == null ? EngineStatus.Failed("TDP Engine unresponsive.") : Read(answer);
        }

        private static EngineStatus Read(string answer)
        {
            if (!JsonObject.TryParse(answer, out var json))
            {
                return EngineStatus.Failed("TDP Engine invalid response.");
            }

            return new EngineStatus
            {
                Reachable = true,
                Running = json.GetNamedBoolean("Running", false),
                Fps = Number(json, "Fps"),
                Watts = (int?)Number(json, "Watts"),
                Temperature = (int?)Number(json, "Temperature"),
                State = Text(json, "State"),
                Error = Text(json, "Error"),
                TargetFps = (int)(Number(json, "TargetFps") ?? 0),
                FanCurve = Text(json, "FanCurve"),
                PowerLimit = (int)(Number(json, "PowerLimit") ?? 0)
            };
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
                return "Windows would not let Game Bar start TDP Engine. Try launching a game from the game library to restart it.";
            }

            try
            {
                var service = OpenService(manager, ServiceName, ServiceStart);
                if (service == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                        ? "TDP Engine is not installed. Install the latest Handheld Optimiser to access it."
                        : "Windows would not let Game Bar start TDP Engine. Try launching a game from the game library to restart it.";
                }

                try
                {
                    if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorAlreadyRunning)
                    {
                        return "Windows would not start TDP Engine.";
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
