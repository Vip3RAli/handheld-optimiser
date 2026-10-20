using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The TDP Engine service. The game library starts it when a game starts with TDP Engine on, and stops it
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

    // A request has this long to arrive and be answered. The library sends one line straight away.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    // Pipe instances, so one client that connects and says nothing does not keep the library out.
    private const int PipeInstances = 4;

    // While a game is held, the operating mode to put back is kept here too, so that if the service dies
    // without putting it back, its next start does. HKLM, which only administrators and the system can write.
    private const string HoldingKey = @"SOFTWARE\HandheldOptimiser\TdpEngine";
    private const string HoldingValue = "RestoreMode";

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

    // Set once it has decided to end, so a start that arrives meanwhile is turned away rather than lost.
    private bool _ending;

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
        Console.WriteLine("TDP Engine is running. Press Enter to stop.");
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
            _ending = true;
            Release("the service stopped");
        }

        _frames?.Dispose();
        _acpi?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        Task? serving = null;
        try
        {
            Log.Write("Started");
            _acpi = AsusAcpi.Open();
            if (_acpi is null || !_acpi.CanSetPowerLimits)
            {
                _error = "This device's power limit cannot be set. TDP Engine works on the ROG Ally with ASUS's drivers installed.";
                Log.Write(_error);
            }
            else
            {
                RestoreAfterCrash();
                try
                {
                    _frames = FrameCounter.Start();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
                {
                    _error = "Windows would not let TDP Engine count frames.";
                    Log.Write($"{_error} {ex.Message}");
                }
            }

            serving = ServeAsync(cancel);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancel))
            {
                lock (_gate)
                {
                    try
                    {
                        Tick();
                    }
                    catch (Exception ex)
                    {
                        // One bad second is not a reason to stop; the next tick tries again.
                        Log.Write($"A second's work failed: {ex.Message}");
                    }

                    if (_control is null && DateTime.UtcNow - _idleSince > IdleExit)
                    {
                        _ending = true;
                        Log.Write("Nothing to do, so ending");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Something the service cannot carry on from: the operating mode goes back, and it ends.
            Log.Write($"TDP Engine failed: {ex}");
            lock (_gate)
            {
                _ending = true;
                try
                {
                    Release("TDP Engine failed");
                }
                catch (Exception releaseError)
                {
                    Log.Write($"Could not put the operating mode back: {releaseError.Message}");
                }
            }
        }

        // Ended on its own: the service manager is told, which calls OnStop to clean up.
        if (!cancel.IsCancellationRequested)
        {
            _ = Task.Run(EndSelf);
        }

        if (serving is not null)
        {
            await serving.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// When the service last ended without putting the operating mode back (it crashed, or was killed), the
    /// game's power limit would otherwise stay set until Armoury Crate changes it. So the mode saved as it
    /// began is put back now.
    /// </summary>
    private void RestoreAfterCrash()
    {
        if (HeldMode() is not { } mode)
        {
            return;
        }

        var restored = _acpi!.SetOperatingMode(mode);
        Log.Write($"The last run ended while holding a game. Operating mode {mode} {(restored ? "put back" : "was refused")}");
        MarkHolding(null);
    }

    private static int? HeldMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HoldingKey);
            return key?.GetValue(HoldingValue) is int mode ? mode : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Saves the operating mode to put back while a game is held, or with null, clears it.</summary>
    private static void MarkHolding(int? mode)
    {
        try
        {
            if (mode is { } value)
            {
                using var key = Registry.LocalMachine.CreateSubKey(HoldingKey);
                key.SetValue(HoldingValue, value, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(HoldingKey, writable: true);
                key?.DeleteValue(HoldingValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not {(mode is null ? "clear" : "save")} the operating mode to put back: {ex.Message}");
        }
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
    private static double GameFps(Dictionary<int, double> frames, string? folder) =>
        AutoTdpRules.GameFps(frames, folder, ProcessPath);

    // ----- Requests from the library -----

    private async Task ServeAsync(CancellationToken cancel)
    {
        // The first instance claims the name, so a program that took it first cannot pose as this service.
        NamedPipeServerStream claimed;
        try
        {
            claimed = CreatePipe(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program holds the name. Nothing can reach this service, so it ends.
            Log.Write($"Could not open the pipe: {ex.Message}");
            _ = Task.Run(EndSelf);
            return;
        }

        var listeners = new List<Task> { ListenAsync(claimed, cancel) };
        for (var i = 1; i < PipeInstances; i++)
        {
            listeners.Add(ListenAsync(null, cancel));
        }

        await Task.WhenAll(listeners).ConfigureAwait(false);
    }

    /// <summary>Answers one connection after another on one pipe instance, until the service ends. Never throws.</summary>
    private async Task ListenAsync(NamedPipeServerStream? pipe, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream current;
            try
            {
                current = pipe ?? CreatePipe(first: false);
                pipe = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Could not open another pipe instance: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await using (current)
                {
                    await current.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(RequestTimeout);
                    await AnswerAsync(current, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // The library went away mid-request.
            }
            catch (Exception ex)
            {
                // Whatever one client did, the pipe stays open for the next.
                Log.Write($"A request failed: {ex.Message}");
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(bool first) =>
        NamedPipeServerStreamAcl.Create(AutoTdpProtocol.PipeName, PipeDirection.InOut, PipeInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0),
            0, 0, PipeAccess());

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var request = AutoTdpProtocol.Read<AutoTdpRequest>(await AutoTdpRules.ReadLineAsync(pipe, cancel).ConfigureAwait(false));
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

        await writer.WriteLineAsync(AutoTdpProtocol.Write(status).AsMemory(), cancel).ConfigureAwait(false);
    }

    private AutoTdpStatus Begin(AutoTdpRequest request, int client)
    {
        if (_error is not null || _acpi is null)
        {
            return new AutoTdpStatus(false, Error: _error);
        }

        // On its way out: the library waits for it to stop and starts it again.
        if (_ending)
        {
            return new AutoTdpStatus(false, Error: AutoTdpProtocol.EndingError);
        }

        // Without the library's process there is no telling when it goes away, and the power limit would
        // stay set until the service is stopped by hand.
        if (ClientHandle(client) is not { } clientProcess)
        {
            return new AutoTdpStatus(false, Error: "TDP Engine could not tell which program asked for it.");
        }

        var (settings, start, folder) = AutoTdpRules.FromRequest(request);

        // A new game keeps the operating mode saved for the first, which is the player's own.
        var restoreMode = _control?.RestoreMode ?? _acpi.OperatingModeNow();
        _control?.ClientProcess?.Dispose();
        _control = new Control(new AutoTdpController(settings, start), folder, clientProcess, restoreMode);

        // Saved before the limit is first set, so a crash from here on still gets the mode put back.
        MarkHolding(restoreMode ?? AsusAcpi.PerformanceMode);
        Apply(_control, _control.Controller.Watts);
        Log.Write($"Holding {settings.TargetFps} fps between {settings.MinWatts} and {settings.MaxWatts} W, under {settings.TemperatureLimit} °C, from {_control.Controller.Watts} W, for {(folder is null ? "the busiest program" : AutoTdpRules.Printable(folder))}");

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
        MarkHolding(null);
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
        // counts as part of writing to a pipe. With several instances allowed, that also lets the player's
        // programs add an instance of their own while one is free, so the library checks that the end it
        // reached belongs to this service's process before it trusts an answer.
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
