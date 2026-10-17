using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
    public static Task<string?> StartAsync(Game game, int targetFps, string fanCurve) => Queue(() =>
    {
        var request = new AutoTdpRequest(AutoTdpProtocol.Start, targetFps, AutoTdpProtocol.LowestWatts,
            LibrarySettings.AutoTdpMaxWatts, LibrarySettings.AutoTdpTemperature, Learned(game),
            GameSession.ProcessFolder(game), fanCurve == AllyFans ? null : fanCurve);

        if (StartService() is { } problem)
        {
            return Task.FromResult<string?>(problem);
        }

        var status = Send(request);
        if (status is null)
        {
            return Task.FromResult<string?>("TDP Engine did not answer.");
        }

        Program.Log($"TDP Engine for {game.Key}: {targetFps} fps from {status.Watts} W, {fanCurve} fans{(status.Error is { } error ? $" ({error})" : "")}");
        return Task.FromResult(status.Error);
    });

    /// <summary>Stops, remembering where the game settled for next time.</summary>
    public static Task StopAsync(Game game) => Queue(() =>
    {
        // Nothing to stop when the service is not running, and asking would start a wait for nothing.
        if (!ServiceRunning())
        {
            return Task.FromResult<string?>(null);
        }

        if (Send(new AutoTdpRequest(AutoTdpProtocol.Stop)) is { Watts: { } watts })
        {
            Remember(game, watts);
        }

        return Task.FromResult<string?>(null);
    });

    /// <summary>A line on what it is doing, or null when it is not running.</summary>
    public static Task<string?> DescribeAsync() => Queue(() =>
    {
        if (!ServiceRunning() || Send(new AutoTdpRequest(AutoTdpProtocol.Status)) is not { Running: true } status)
        {
            return Task.FromResult<string?>(null);
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

        return Task.FromResult<string?>(string.Join(", ", parts));
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

    private static AutoTdpStatus? Send(AutoTdpRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AutoTdpProtocol.PipeName, PipeDirection.InOut);
            pipe.Connect(ConnectWait);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(AutoTdpProtocol.Write(request));
            return AutoTdpProtocol.Read<AutoTdpStatus>(reader.ReadLine());
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
    private const int ServiceRunningState = 4;
    private const int ErrorAlreadyRunning = 1056;
    private const int ErrorDoesNotExist = 1060;
    private const int ErrorAccessDenied = 5;

    /// <returns>Why it could not be started, or null when it is running or starting.</returns>
    private static string? StartService()
    {
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
                if (!StartServiceW(service, 0, 0) && Marshal.GetLastPInvokeError() is var code and not ErrorAlreadyRunning)
                {
                    return $"Windows would not start TDP Engine ({new Win32Exception(code).Message}).";
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

    private static bool ServiceRunning()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, AutoTdpProtocol.ServiceName, ServiceQueryStatus);
            if (service == 0)
            {
                return false;
            }

            try
            {
                // SERVICE_STATUS: the second of its seven numbers is the state.
                Span<int> status = stackalloc int[7];
                return QueryServiceStatus(service, ref MemoryMarshal.GetReference(status)) && status[1] == ServiceRunningState;
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

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint service, uint argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, ref int status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
