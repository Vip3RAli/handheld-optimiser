using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HandheldOptimiser.PowerService;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// TDP Engine from the library's side. Counting a game's frames needs administrator, which the library
/// never has, so the work is done by the TDP Engine service the installer registers; the library starts it
/// as a game starts and tells it which game and what to hold. The player's account may start that
/// service, and talk to it, but nothing more. Requests go one at a time, in the order they were made.
/// </summary>
internal static partial class AutoTdp
{
    private const string LearnedKey = Program.SettingsKey + @"\AutoTdp";

    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(5);

    // The service answers at once; a reply this late means it is stuck, and the library should not be.
    private static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(5);

    // How long a service on its way out is given to finish stopping before it is started again.
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(8);

    private static readonly object Gate = new();
    private static Task _queue = Task.CompletedTask;

    /// <summary>The frame rates on offer. 0 is off.</summary>
    public static readonly int[] FrameRates = [30, 40, 45, 60];

    public static readonly int[] TemperatureLimits = [70, 75, 80, 85, 90];

    public static readonly int[] MostWatts = [15, 18, 20, 25, 30];

    /// <summary>The Ally's own fan curve, which TDP Engine leaves alone.</summary>
    public const string AllyFans = "default";

    /// <summary>The fan curves on offer, the Ally's own first.</summary>
    public static readonly string[] FanChoices = [AllyFans, .. AutoTdpProtocol.FanModes];

    public static string FanName(string fans) => fans switch
    {
        AutoTdpProtocol.QuietFans => "Quiet",
        AutoTdpProtocol.CoolFans => "Cool",
        AutoTdpProtocol.FullFans => "Max",
        AutoTdpProtocol.DynamicFans => "Dynamic",
        _ => "Default"
    };

    /// <summary>Starts holding a game at its target, from what it settled on last time, and setting its fan curve.</summary>
    /// <param name="targetFps">The frame rate to hold, or 0 to set only the fans.</param>
    /// <returns>Why it could not start, or null.</returns>
    public static Task<string?> StartAsync(Game game, int targetFps, string fanCurve) => Queue(async () =>
    {
        var request = new AutoTdpRequest(AutoTdpProtocol.Start, targetFps, AutoTdpProtocol.LowestWatts,
            LibrarySettings.AutoTdpMaxWatts, LibrarySettings.AutoTdpTemperature, Learned(game),
            GameSession.ProcessFolder(game), fanCurve == AllyFans ? null : fanCurve);

        if (await StartServiceAsync() is { } problem)
        {
            return problem;
        }

        var status = await SendAsync(request);

        // It had just decided to end, after the last game, as this one started. Once it has stopped it is
        // started again, rather than the game going without.
        if (status?.Error == AutoTdpProtocol.EndingError)
        {
            await WaitForStoppedAsync();
            if (await StartServiceAsync() is { } again)
            {
                return again;
            }

            status = await SendAsync(request);
        }

        if (status is null)
        {
            return "TDP Engine did not answer.";
        }

        Program.Log($"TDP Engine for {game.Key}: {targetFps} fps from {status.Watts} W, {fanCurve} fans{(status.Error is { } error ? $" ({error})" : "")}");
        return status.Error;
    });

    /// <summary>Stops, remembering where the game settled for next time.</summary>
    public static Task StopAsync(Game game) => Queue(async () =>
    {
        // Nothing to stop when the service is not running, and asking would start a wait for nothing.
        if (!ServiceRunning())
        {
            return null;
        }

        if (await SendAsync(new AutoTdpRequest(AutoTdpProtocol.Stop)) is { Watts: { } watts })
        {
            Remember(game, watts);
        }

        return null;
    });

    /// <summary>A line on what it is doing, or null when it is not running.</summary>
    /// <summary>TDP Engine's status, with the controller's state while it replaces Armoury Crate, or null when it is not running.</summary>
    public static async Task<AutoTdpStatus?> StatusAsync() =>
        ServiceRunning() ? await SendAsync(new AutoTdpRequest(AutoTdpProtocol.Status)) : null;

    /// <summary>Puts the controller in a mode; with <paramref name="temporary"/>, without saving it.</summary>
    /// <returns>The status, its Error a notice when the mode could not be sent, or null when TDP Engine did not answer.</returns>
    public static Task<AutoTdpStatus?> SetControllerModeAsync(string mode, bool temporary) =>
        SendAsync(new AutoTdpRequest(AutoTdpProtocol.SetControls, ControllerMode: mode, Temporary: temporary));

    /// <summary>Ends a temporary hold: the controller goes back to the mode the player saved, whatever it is now.</summary>
    public static Task<AutoTdpStatus?> ReleaseControllerModeAsync() =>
        SendAsync(new AutoTdpRequest(AutoTdpProtocol.SetControls, Temporary: true));

    public static Task<string?> DescribeAsync() => Queue(async () =>
    {
        if (!ServiceRunning() || await SendAsync(new AutoTdpRequest(AutoTdpProtocol.Status)) is not { Running: true } status)
        {
            return null;
        }

        var parts = new List<string>();
        if (status.Watts is { } watts)
        {
            parts.Add($"TDP Engine {watts} W");
            if (status.Fps is { } fps)
            {
                parts.Add($"{fps:0} fps");
            }
        }

        if (status.FanCurve is { } fans)
        {
            parts.Add($"{FanName(fans)} fans");
        }

        if (status.Temperature is { } degrees)
        {
            parts.Add($"{degrees} °C");
        }

        if (Explain(status.State) is { } why)
        {
            parts.Add(why);
        }

        return string.Join(", ", parts);
    });

    private static string? Explain(string? state) => Enum.TryParse<AutoTdpState>(state, out var parsed) ? parsed switch
    {
        AutoTdpState.TooHot => "cooling down",
        AutoTdpState.AtMaximum => "at its most watts",
        AutoTdpState.NoGain => "more power does not help this game",
        _ => null
    } : null;

    private static Task<string?> Queue(Func<Task<string?>> work)
    {
        lock (Gate)
        {
            var next = _queue.ContinueWith(_ => Task.Run(work), TaskScheduler.Default).Unwrap();
            _queue = next;
            return next;
        }
    }

    private static async Task<AutoTdpStatus?> SendAsync(AutoTdpRequest request)
    {
        try
        {
            // Identification only: the service may see who is asking, never act as them.
            using var pipe = new NamedPipeClientStream(".", AutoTdpProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync((int)ConnectWait.TotalMilliseconds);

            // Any program of the player's could open a pipe of the same name if the service is not holding
            // it, so the answer only counts from the service's own process.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) ||
                ServiceStatus() is not { ProcessId: > 0 } service || service.ProcessId != serverId)
            {
                Program.Log($"TDP Engine {request.Command} refused: the pipe does not belong to the TDP Engine service");
                return null;
            }

            using var reply = new CancellationTokenSource(ReplyWait);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(AutoTdpProtocol.Write(request).AsMemory(), reply.Token);
            return AutoTdpProtocol.Read<AutoTdpStatus>(await reader.ReadLineAsync(reply.Token));
        }
        catch (OperationCanceledException)
        {
            Program.Log($"TDP Engine {request.Command} failed: no answer within {ReplyWait.TotalSeconds:0} seconds");
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Program.Log($"TDP Engine {request.Command} failed: {ex.Message}");
            return null;
        }
    }

    // ----- What each game settled on -----

    private static int Learned(Game game)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LearnedKey);
            return key?.GetValue(game.Key) is int watts ? watts : 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    private static void Remember(Game game, int watts)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LearnedKey);
            key.SetValue(game.Key, watts, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not remember TDP Engine's watts for {game.Key}: {ex.Message}");
        }
    }

    // ----- The service -----

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ServiceStoppedState = 1;
    private const int ServiceStopPendingState = 3;
    private const int ServiceRunningState = 4;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorAlreadyRunning = 1056;
    private const int ErrorDoesNotExist = 1060;
    private const int ErrorAccessDenied = 5;

    /// <returns>Why it could not be started, or null when it is running or starting.</returns>
    private static async Task<string?> StartServiceAsync()
    {
        var problem = TryStartService(out var stopping);

        // Still on its way out from the last game: Windows will not start it again until it has stopped.
        if (problem is null && stopping)
        {
            await WaitForStoppedAsync();
            problem = TryStartService(out _);
        }

        return problem;
    }

    /// <param name="stopping">Whether Windows said it was running because it was still stopping.</param>
    private static string? TryStartService(out bool stopping)
    {
        stopping = false;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return "Windows would not start TDP Engine.";
        }

        try
        {
            var service = OpenService(manager, AutoTdpProtocol.ServiceName, ServiceStart | ServiceQueryStatus);
            if (service == 0)
            {
                return Marshal.GetLastPInvokeError() switch
                {
                    ErrorDoesNotExist => "TDP Engine is not installed. Install the latest Handheld Optimiser to add it.",
                    ErrorAccessDenied => "Windows would not let the library start TDP Engine. Installing Handheld Optimiser again fixes this.",
                    var code => $"Windows would not start TDP Engine ({new Win32Exception(code).Message})."
                };
            }

            try
            {
                if (!StartServiceW(service, 0, 0))
                {
                    var code = Marshal.GetLastPInvokeError();
                    if (code != ErrorAlreadyRunning)
                    {
                        return $"Windows would not start TDP Engine ({new Win32Exception(code).Message}).";
                    }

                    stopping = Status(service) is { State: ServiceStopPendingState };
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

    /// <summary>Waits a few seconds at most for the service to finish stopping.</summary>
    private static async Task WaitForStoppedAsync()
    {
        var until = DateTime.UtcNow + StopWait;
        while (DateTime.UtcNow < until)
        {
            if (ServiceStatus() is not { State: not ServiceStoppedState })
            {
                return;
            }

            await Task.Delay(200);
        }

        Program.Log("TDP Engine was still stopping after waiting for it");
    }

    private static bool ServiceRunning() => ServiceStatus() is { State: ServiceRunningState };

    /// <summary>The service's state and process id, or null when they cannot be read.</summary>
    private static (int State, int ProcessId)? ServiceStatus()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return null;
        }

        try
        {
            var service = OpenService(manager, AutoTdpProtocol.ServiceName, ServiceQueryStatus);
            if (service == 0)
            {
                return null;
            }

            try
            {
                return Status(service);
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

    private static (int State, int ProcessId)? Status(nint service)
    {
        // SERVICE_STATUS_PROCESS: of its nine numbers, the second is the state and the eighth the process id.
        Span<int> status = stackalloc int[9];
        return QueryServiceStatusEx(service, ScStatusProcessInfo, ref MemoryMarshal.GetReference(status), status.Length * sizeof(int), out _)
            ? (status[1], status[7])
            : null;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint service, uint argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int infoLevel, ref int buffer, int size, out int needed);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
