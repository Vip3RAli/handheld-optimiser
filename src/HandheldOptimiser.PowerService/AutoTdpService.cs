using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Auto TDP service. The game library starts it when a game starts with Auto TDP on, and stops it
/// when the game closes; it never runs otherwise. Each second it counts the game's frames, reads the
/// processor's temperature, and sets the power limit the controller asks for. Whatever happens, it puts
/// Armoury Crate's operating mode back as it ends, which brings back that mode's own power limits.
/// </summary>
internal sealed partial class AutoTdpService : ServiceBase
{
    // With nothing to do for this long, it ends: started by mistake, or the library went away.
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(1);

    // Armoury Crate can put its own limits back, on a charger being plugged in say, so they are set again this often.
    private static readonly TimeSpan Reassert = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _running;

    private AsusAcpi? _acpi;
    private FrameCounter? _frames;
    private string? _error;

    // The game being controlled, or null between games.
    private Control? _control;
    private DateTime _idleSince = DateTime.UtcNow;
    private AutoTdpStatus _last = new(false);

    public AutoTdpService()
    {
        ServiceName = AutoTdpProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
    }

    protected override void OnStart(string[] args) => _running = Task.Run(() => RunAsync(_stop.Token));

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        // The firmware can come back from sleep with its own limits, so they are set again straight away.
        if (powerStatus == PowerBroadcastStatus.ResumeSuspend)
        {
            lock (_gate)
            {
                if (_control is { } control)
                {
                    control.LastSet = DateTime.MinValue;
                }
            }
        }

        return true;
    }

    /// <summary>How it ends itself: through the service manager, or from a console by cancelling.</summary>
    private Action EndSelf => _console ? _stop.Cancel : Stop;

    private bool _console;

    /// <summary>For trying it from a console as administrator, with --console.</summary>
    public void RunInConsole()
    {
        _console = true;
        OnStart([]);
        Console.WriteLine("Auto TDP is running. Press Enter to stop.");
        Console.ReadLine();
        Shutdown();
    }

    private void Shutdown()
    {
        _stop.Cancel();
        try
        {
            _running?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Logged where it happened.
        }

        lock (_gate)
        {
            Release("the service stopped");
        }

        _frames?.Dispose();
        _acpi?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        Log.Write("Started");
        _acpi = AsusAcpi.Open();
        if (_acpi is null || !_acpi.CanSetPowerLimits)
        {
            _error = "This device's power limit cannot be set. Auto TDP works on the ROG Ally with ASUS's drivers installed.";
            Log.Write(_error);
        }
        else
        {
            try
            {
                _frames = FrameCounter.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                _error = "Windows would not let Auto TDP count frames.";
                Log.Write($"{_error} {ex.Message}");
            }
        }

        var serving = ServeAsync(cancel);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancel))
            {
                lock (_gate)
                {
                    Tick();
                    if (_control is null && DateTime.UtcNow - _idleSince > IdleExit)
                    {
                        Log.Write("Nothing to do, so ending");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        // Ended on its own: the service manager is told, which calls OnStop to clean up.
        if (!cancel.IsCancellationRequested)
        {
            _ = Task.Run(EndSelf);
        }

        await serving.ConfigureAwait(false);
    }

    /// <summary>One second: the game's frame rate and the temperature to the controller, and its answer to the firmware.</summary>
    private void Tick()
    {
        var frames = _frames?.Take();
        if (_control is not { } control || _acpi is null)
        {
            return;
        }

        if (!ClientAlive(control.ClientProcess))
        {
            Release("the game library closed");
            return;
        }

        var fps = frames is null ? 0 : GameFps(frames, control.GameFolder);
        var temperature = _acpi.Temperature();
        var now = DateTime.UtcNow;

        if (control.Controller.Tick(now, fps, temperature) is { } watts)
        {
            Apply(control, watts);
            Log.Write($"{watts} W: {fps:0} fps, {temperature?.ToString() ?? "?"} °C, {control.Controller.State}");
        }
        else if (now - control.LastSet >= Reassert)
        {
            Apply(control, control.Controller.Watts);
        }

        _last = Status(control, fps, temperature);
    }

    private void Apply(Control control, int watts)
    {
        if (!_acpi!.SetPowerLimit(watts))
        {
            Log.Write($"The firmware refused {watts} W");
        }

        control.LastSet = DateTime.UtcNow;
    }

    private static AutoTdpStatus Status(Control control, double? fps, int? temperature) =>
        new(true, fps, control.Controller.Watts, temperature, control.Controller.State.ToString());

    /// <summary>
    /// The game's frames a second: the busiest program running from its folder. Without a folder, the
    /// busiest program of all but Windows' own compositor, whose frames are every window's.
    /// </summary>
    private static double GameFps(Dictionary<int, double> frames, string? folder)
    {
        var best = 0.0;
        foreach (var (processId, fps) in frames)
        {
            if (fps <= best)
            {
                continue;
            }

            var path = ProcessPath(processId);
            if (path is null)
            {
                continue;
            }

            var counts = folder is not null
                ? path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                : !string.Equals(Path.GetFileName(path), "dwm.exe", StringComparison.OrdinalIgnoreCase);

            if (counts)
            {
                best = fps;
            }
        }

        return best;
    }

    // ----- Requests from the library -----

    private async Task ServeAsync(CancellationToken cancel)
    {
        var first = true;
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(AutoTdpProtocol.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0),
                    0, 0, PipeAccess());
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another program holds the name. Nothing can reach this service, so it ends.
                Log.Write($"Could not open the pipe: {ex.Message}");
                _ = Task.Run(EndSelf);
                return;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancel);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await AnswerAsync(pipe, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                    // The library went away mid-request.
                }
            }
        }
    }

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var request = AutoTdpProtocol.Read<AutoTdpRequest>(await reader.ReadLineAsync(cancel));
        var client = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : 0;

        AutoTdpStatus status;
        lock (_gate)
        {
            status = request?.Command switch
            {
                AutoTdpProtocol.Start => Begin(request, client),
                AutoTdpProtocol.Stop => End(),
                _ => _error is not null ? new AutoTdpStatus(false, Error: _error) : _last
            };
        }

        await writer.WriteLineAsync(AutoTdpProtocol.Write(status).AsMemory(), cancel);
    }

    private AutoTdpStatus Begin(AutoTdpRequest request, int client)
    {
        if (_error is not null || _acpi is null)
        {
            return new AutoTdpStatus(false, Error: _error);
        }

        var min = Math.Clamp(request.MinWatts, AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts);
        var max = Math.Clamp(request.MaxWatts, min, AutoTdpProtocol.HighestWatts);
        var settings = new AutoTdpSettings(
            Math.Clamp(request.TargetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps),
            min,
            max,
            Math.Clamp(request.TemperatureLimit, AutoTdpProtocol.LowestTemperature, AutoTdpProtocol.HighestTemperature));

        var start = request.StartWatts > 0 ? request.StartWatts : (min + max + 1) / 2;
        var folder = string.IsNullOrWhiteSpace(request.GameFolder) ? null : request.GameFolder;

        // A new game keeps the operating mode saved for the first, which is the player's own.
        var restoreMode = _control?.RestoreMode ?? _acpi.OperatingModeNow();
        _control = new Control(new AutoTdpController(settings, start), folder, ClientHandle(client), restoreMode);
        Apply(_control, _control.Controller.Watts);
        Log.Write($"Holding {settings.TargetFps} fps between {min} and {max} W, under {settings.TemperatureLimit} °C, from {_control.Controller.Watts} W, for {folder ?? "the busiest program"}");

        _last = Status(_control, null, _acpi.Temperature());
        return _last;
    }

    private AutoTdpStatus End()
    {
        Release("the game closed");
        return _last;
    }

    /// <summary>Stops controlling and puts the operating mode back. Hold _gate.</summary>
    private void Release(string why)
    {
        if (_control is not { } control)
        {
            return;
        }

        _control = null;
        _idleSince = DateTime.UtcNow;
        _last = _last with { Running = false };
        control.ClientProcess?.Dispose();

        // Setting the mode again brings back its own power limits. When it could not be read, Performance,
        // Armoury Crate's own default, is put back instead.
        var mode = control.RestoreMode ?? AsusAcpi.PerformanceMode;
        var restored = _acpi!.SetOperatingMode(mode);
        Log.Write($"Stopped, as {why}. Operating mode {mode} {(restored ? "put back" : "was refused")}");
    }

    private static Process? ClientHandle(int processId)
    {
        try
        {
            return processId > 0 ? Process.GetProcessById(processId) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ClientAlive(Process? client)
    {
        try
        {
            return client is null || !client.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>The system and administrators own the pipe; the signed-in player may only talk to it.</summary>
    private static PipeSecurity PipeAccess()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // A client opening it to read and write is checked for CreateNewInstance as well, which Windows
        // counts as part of writing to a pipe.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    private static unsafe string? ProcessPath(int processId)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            const int Length = 1024;
            var buffer = stackalloc char[Length];
            var size = (uint)Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <param name="RestoreMode">Armoury Crate's operating mode from before, to put back, or null when it could not be read.</param>
    private sealed record Control(AutoTdpController Controller, string? GameFolder, Process? ClientProcess, int? RestoreMode)
    {
        public DateTime LastSet { get; set; }
    }

    private const uint QueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* path, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
