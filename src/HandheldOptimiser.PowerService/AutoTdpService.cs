using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The TDP Engine service. The game library starts it when a game starts with TDP Engine or a fan curve
/// on, and stops it when the game closes; the Game Bar widget can start it too. It never runs otherwise.
/// Each second it counts the game's frames, reads the processor's temperature, and sets the power limit
/// the controller asks for, and the fan curve. Whatever happens, it puts Armoury Crate's operating mode
/// back as it ends, which brings back that mode's own power limits and fan curve.
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

    // Started from Game Bar, there is no library to say the game closed, so it stops after this long without one.
    private static readonly TimeSpan NoGameExit = TimeSpan.FromMinutes(1);
    private const double GameFrames = 5;

    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _running;

    // The Ally's buttons and controller mode, run only while the app stands in for Armoury Crate. Read once
    // as it starts: switching that on or off restarts the service.
    private AllyControlsHost? _controls;

    // The maker's firmware, through which the power limit and the fans are set.
    private IPowerBackend? _power;
    private FrameCounter? _frames;
    private string? _error;

    // The performance overlay, and what it is told each second: every program's frames in the last second,
    // and the temperature, read only while the overlay is on.
    private readonly OverlayHost _overlay = new();
    private Dictionary<int, double>? _lastFrames;
    private int? _overlayTemperature;

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
                    control.LastFans = DateTime.MinValue;
                }
            }

            // Outside the gate, as it closes the controller's handle.
            _controls?.Resume();
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

        _controls?.Dispose();
        _frames?.Dispose();
        _power?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        Task? serving = null;
        try
        {
            Log.Write("Started");
            if (AllyControlsStore.Replacing())
            {
                Log.Write("Replacing Armoury Crate");
                _controls = new AllyControlsHost();
                _controls.Start(cancel);
            }

            _power = PowerBackends.Open();
            if (_power is null)
            {
                _error = "TDP Engine cannot set the power limit on this device. It works on the ROG Ally with ASUS's drivers installed.";
                Log.Write(_error);
            }
            else
            {
                Log.Write($"Power limits through {_power.Name}");
                RestoreAfterCrash();
            }

            // Frames are counted on any device, for the overlay, even where the power limit cannot be set.
            try
            {
                _frames = FrameCounter.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                var problem = "Windows did not let TDP Engine read the frame rate. Restart the handheld and try again.";
                _error ??= problem;
                Log.Write($"{problem} {ex.Message}");
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

                    // While replacing Armoury Crate it runs the controller too, and while the overlay is on it
                    // feeds it, so it stays.
                    if (_controls is null && _control is null && !_overlay.Wanted && DateTime.UtcNow - _idleSince > IdleExit)
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

            // While it stands in for Armoury Crate, the Ally's buttons need it running. A clean stop never fires
            // the service's failure actions, so it ends with an error instead, and the service manager starts it
            // again (sc.exe failureflag 1, set by Replace Armoury Crate and the installer).
            if (_controls is not null)
            {
                ExitCode = 1;
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

        var restored = _power!.SetVendorMode(mode);
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
        _lastFrames = frames;
        if (_overlay.Wanted)
        {
            _overlayTemperature = _power?.ReadTemperature();
            _overlay.Keep();
        }

        if (_control is not { } control || _power is null)
        {
            return;
        }

        if (!ClientAlive(control.ClientProcess))
        {
            Release("the game library closed");
            return;
        }

        var (fps, program) = frames is null ? (0, null) : GameFps(frames, control.GameFolder ?? control.FoundFolder, control.LibraryFolders);
        var temperature = _power.ReadTemperature();
        var now = DateTime.UtcNow;

        if (control.Lock?.Observe(now, program, fps) is { } found)
        {
            control.FoundFolder = found;
            control.Lock = null;
            Log.Write($"Following {found}, which held {fps:0} fps");
        }

        if (fps >= GameFrames)
        {
            control.LastFrames = now;
        }
        else if (control.FromWidget && now - control.LastFrames > NoGameExit)
        {
            Release("no game was running");
            return;
        }

        if (control.Controller is { } controller)
        {
            if (controller.Tick(now, fps, temperature) is { } watts)
            {
                Apply(control, watts);
                // The frame rate the change went on, not this second's count, which can be far from it.
                Log.Write($"{watts} W: {controller.DecidedFps ?? fps:0} fps, {temperature?.ToString() ?? "?"} °C, {controller.State}");
            }
            else if (now - control.LastSet >= Reassert)
            {
                Apply(control, controller.Watts);
            }

            Log.Second(now, control.GameFolder ?? control.FoundFolder, control.Settings?.TargetFps ?? 0, fps, temperature,
                controller.Watts, controller.State);
        }
        else if (control.FixedWatts is { } fixedWatts && now - control.LastSet >= Reassert)
        {
            Apply(control, fixedWatts);
        }

        if (control.Dynamic is { } dynamic && control.Curve is not null)
        {
            var pick = dynamic.Pick(now, temperature, WattsNow(control), TopWatts(control), TemperatureLimit(control));
            if (pick != control.DynamicPick)
            {
                control.DynamicPick = pick;
                control.Curve = FanCurves.For(pick);
                ApplyFans(control);
                Log.Write($"Dynamic fans: {pick}, {temperature?.ToString() ?? "?"} °C{(WattsNow(control) is { } held ? $", {held} W" : "")}");
            }
        }

        if (control.Curve is not null && now - control.LastFans >= Reassert)
        {
            ApplyFans(control);
        }

        _last = Status(control, fps, temperature);
    }

    /// <summary>
    /// Sets which fans a game runs with. Dynamic picks its first curve straight away, and keeps the one it
    /// has when the widget changes only the frame rate, so its 30 second hold is not lost.
    /// </summary>
    private static void ChooseFans(Control control, string? fans, int? temperature)
    {
        control.FanCurve = fans;
        if (fans != AutoTdpProtocol.DynamicFans)
        {
            control.Dynamic = null;
            control.DynamicPick = null;
            control.Curve = FanCurves.For(fans);
            return;
        }

        control.Dynamic ??= new DynamicFans();
        control.DynamicPick = control.Dynamic.Pick(DateTime.UtcNow, temperature, WattsNow(control),
            TopWatts(control), TemperatureLimit(control));
        control.Curve = FanCurves.For(control.DynamicPick);
    }

    // With fans only there are no settings, so Dynamic measures from the default limit.
    private static int TemperatureLimit(Control control) =>
        control.Settings?.TemperatureLimit ?? AutoTdpProtocol.DefaultTemperature;

    /// <summary>The power limit set now: TDP Engine's, or a fixed one from the widget. Null when Armoury Crate sets it.</summary>
    private static int? WattsNow(Control control) => control.Controller?.Watts ?? control.FixedWatts;

    /// <summary>The most it could be: the top of TDP Engine's range, or the highest allowed for a fixed limit. 0 when Armoury Crate sets it.</summary>
    private static int TopWatts(Control control) =>
        control.Settings?.MaxWatts ?? (control.FixedWatts is null ? 0 : AutoTdpProtocol.HighestWatts);

    private void Apply(Control control, int watts)
    {
        if (!_power!.SetPowerLimit(watts))
        {
            Log.Write($"The firmware refused {watts} W");
        }

        control.LastSet = DateTime.UtcNow;
    }

    private void ApplyFans(Control control)
    {
        control.LastFans = DateTime.UtcNow;
        if (_power!.SetFanCurve(control.Curve!))
        {
            return;
        }

        // Not asked again: a model without custom fan curves refuses every time.
        Log.Write($"The firmware refused the {control.FanCurve} fan curve, so the fans are left as they were");
        control.Curve = null;
        control.Dynamic = null;
    }

    private static AutoTdpStatus Status(Control control, double? fps, int? temperature) =>
        new(true, fps, WattsNow(control), temperature,
            control.Controller?.State.ToString() ?? (control.FixedWatts is null ? null : AutoTdpProtocol.FixedState),
            TargetFps: control.Settings?.TargetFps ?? 0, FanCurve: control.Curve is null ? null : control.FanCurve,
            PowerLimit: control.PowerLimit);

    /// <summary>
    /// The game's frames a second: the busiest program running from its folder. Without a folder, the
    /// busiest program of all but Windows' own compositor, whose frames are every window's.
    /// </summary>
    /// <param name="library">Until the game's folder is known, the library's game folders. See AutoTdpRules.GameFps.</param>
    private static (double Fps, string? Program) GameFps(Dictionary<int, double> frames, string? folder, string[]? library) =>
        AutoTdpRules.GameFps(frames, folder, library, ProcessPath);

    // ----- Requests from the library -----

    private async Task ServeAsync(CancellationToken cancel)
    {
        // The first instance claims the name, so a program that took it first cannot pose as this service.
        NamedPipeServerStream claimed;
        try
        {
            claimed = CreatePipe(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var request = AutoTdpProtocol.Read<AutoTdpRequest>(await AutoTdpRules.ReadLineAsync(pipe, cancel).ConfigureAwait(false));
        var client = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : 0;

        AutoTdpStatus status;
        if (request?.Command == AutoTdpProtocol.SetControls)
        {
            // Not under the gate, as it writes to the controller.
            status = SetControls(request);
        }
        else if (request?.Command == AutoTdpProtocol.SetBoost)
        {
            // Not under the gate either: it changes Windows' power plan, not anything the engine holds.
            status = SetBoost(request);
        }
        else
        {
            lock (_gate)
            {
                status = request?.Command switch
                {
                    AutoTdpProtocol.Start => Begin(request, client),
                    AutoTdpProtocol.Update => Update(request, client),
                    AutoTdpProtocol.Stop => End(),
                    AutoTdpProtocol.SetOverlay => SetOverlay(request),
                    AutoTdpProtocol.Overlay => OverlayStatus(request),
                    _ => _error is not null ? new AutoTdpStatus(false, Error: _error) : _last
                };
            }
        }

        // Read each time, as the player or another program can change the power plan.
        status = WithControls(status) with
        {
            CpuBoost = CpuBoost.Read(),
            OverlayLevel = _overlay.Level,
            OverlayCorner = _overlay.Corner,
            OverlayLayout = _overlay.Layout
        };
        await writer.WriteLineAsync(AutoTdpProtocol.Write(status).AsMemory(), cancel).ConfigureAwait(false);
    }

    /// <summary>A new overlay level, corner or layout, from the widget. Called under the gate.</summary>
    private AutoTdpStatus SetOverlay(AutoTdpRequest request)
    {
        _overlay.Set(request.OverlayLevel, request.OverlayCorner, request.OverlayLayout);
        _idleSince = DateTime.UtcNow;
        return _error is not null && _control is null ? new AutoTdpStatus(false, Error: _error) : _last;
    }

    /// <summary>
    /// What the overlay shows from here, each second: the frame rate of the program in front, the power limit
    /// TDP Engine holds (none when it holds none), and the temperature. Called under the gate.
    /// </summary>
    private AutoTdpStatus OverlayStatus(AutoTdpRequest request)
    {
        double? fps = _lastFrames is { } frames ? frames.GetValueOrDefault(request.ProcessId) : null;
        int? watts = _control is { } control ? WattsNow(control) : null;
        return _last with { Fps = fps, Watts = watts, Temperature = _overlayTemperature };
    }

    /// <summary>
    /// Turns the processor's boost on or off, from the widget. Answered with the last status, its Error
    /// saying why when Windows would not change it.
    /// </summary>
    private AutoTdpStatus SetBoost(AutoTdpRequest request)
    {
        AutoTdpStatus last;
        lock (_gate)
        {
            last = _last;
        }

        if (request.CpuBoost is not { } on)
        {
            return last with { Error = "The request did not say whether to turn boost on or off." };
        }

        return CpuBoost.Set(on) is { } problem ? last with { Error = problem } : last;
    }

    /// <summary>
    /// A new controller mode, from the widget or the library alike: both are the player. Answered with the
    /// last status, its Error the notice when the mode could not be sent now, or the service's own error otherwise.
    /// </summary>
    private AutoTdpStatus SetControls(AutoTdpRequest request)
    {
        AutoTdpStatus last;
        string? error;
        lock (_gate)
        {
            last = _last;
            error = _error;
        }

        if (_controls is not { } controls)
        {
            return last with { Error = "Turn on Replace ASUS Services with Handheld Library and widget in Handheld Optimiser to use these buttons." };
        }

        // Temporary with no mode: Game Bar has closed, so the hold ends and the saved mode comes back.
        if (request is { Temporary: true, ControllerMode: null })
        {
            return last with { Error = controls.Release() ?? error };
        }

        if (AllyCommands.ParseMode(request.ControllerMode) is not { } mode)
        {
            return last with { Error = "That controller mode is not supported." };
        }

        // The service's own standing error, such as a power limit it cannot set, is kept when the mode went
        // through, so the widget still shows it.
        return last with { Error = controls.SetMode(mode, request.Temporary) ?? error };
    }

    /// <summary>The status with the controller's state, while replacing Armoury Crate. Otherwise as it is.</summary>
    private AutoTdpStatus WithControls(AutoTdpStatus status) => _controls is { } controls
        ? status with
        {
            Replacing = true,
            ControllerConnected = controls.Connected,
            ControllerMode = controls.Mode is { } mode ? AllyCommands.ModeName(mode) : null
        }
        : status;

    /// <summary>A game starting from the library, or from the widget when nothing is running.</summary>
    private AutoTdpStatus Begin(AutoTdpRequest request, int client, bool fromWidget = false)
    {
        if (_error is not null || _power is null)
        {
            return new AutoTdpStatus(false, Error: _error);
        }

        // On its way out: the library waits for it to stop and starts it again.
        if (_ending)
        {
            return new AutoTdpStatus(false, Error: AutoTdpProtocol.EndingError);
        }

        // Without the library's process there is no telling when it goes away, and the power limit would
        // stay set until the service is stopped by hand. A session from the widget is ended instead when
        // no game has drawn for a minute, so it needs none.
        Process? clientProcess = null;
        if (!fromWidget)
        {
            if (ClientHandle(client) is not { } asking)
            {
                return new AutoTdpStatus(false, Error: "TDP Engine could not tell which program asked for it.");
            }

            clientProcess = asking;
        }

        var auto = Settings(request);
        var power = PowerLimit(request);
        var settings = auto is null ? null : PowerLimits.WithTop(auto, power);
        var fixedWatts = PowerLimits.Fixed(request.TargetFps, power);
        var fans = FanCurve(request.FanCurve);
        if (settings is null && fixedWatts is null && fans is null)
        {
            clientProcess?.Dispose();
            Release("there was nothing to set");
            return _last;
        }

        var folder = string.IsNullOrWhiteSpace(request.GameFolder) ? null : request.GameFolder;
        AutoTdpController? controller = null;
        if (settings is not null)
        {
            // 0 starts halfway; what a game learnt is held between the two limits, which may have moved since.
            var start = request.StartWatts > 0
                ? Math.Clamp(request.StartWatts, settings.MinWatts, settings.MaxWatts)
                : (settings.MinWatts + settings.MaxWatts + 1) / 2;
            controller = new AutoTdpController(settings, start);
        }

        // A new game keeps the operating mode saved for the first, which is the player's own.
        var restoreMode = _control?.RestoreMode ?? _power.ReadVendorMode();
        _control?.ClientProcess?.Dispose();
        _control = new Control(settings, controller, folder, clientProcess, restoreMode, fromWidget)
        {
            AutoSettings = auto,
            PowerLimit = power,
            FixedWatts = fixedWatts,
            Lock = folder is null ? new GameLock() : null,
            LibraryFolders = folder is null && fromWidget ? ClientGameFolders(client) : null,
            LastFrames = DateTime.UtcNow
        };
        ChooseFans(_control, fans, _power.ReadTemperature());

        // Saved before the limit is first set, so a crash from here on still gets the mode put back.
        MarkHolding(restoreMode ?? _power.DefaultVendorMode);
        Set(_control);
        var target = (folder is null ? null : AutoTdpRules.Printable(folder))
            ?? (_control.LibraryFolders is { } library ? $"the busiest of the library's {library.Length} games" : "the busiest program");
        Log.Write($"{Describe(_control)}, for {target}{(fromWidget ? ", from Game Bar" : "")}");
        _last = Status(_control, null, _power.ReadTemperature());
        return _last;
    }

    /// <summary>
    /// From the Game Bar widget: a new frame rate or fan curve for what is running, keeping its game and the
    /// operating mode to put back, or a start when nothing is.
    /// </summary>
    private AutoTdpStatus Update(AutoTdpRequest request, int client)
    {
        if (_error is not null || _power is null)
        {
            return new AutoTdpStatus(false, Error: _error);
        }

        if (_control is not { } old)
        {
            return Begin(request with { GameFolder = null, StartWatts = 0 }, client, fromWidget: true);
        }

        var target = request.TargetFps > 0 ? Math.Clamp(request.TargetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps) : 0;
        var power = PowerLimit(request);
        var fixedWatts = PowerLimits.Fixed(target, power);
        var fans = FanCurve(request.FanCurve);
        if (target == 0 && fixedWatts is null && fans is null)
        {
            Release("TDP Engine was turned off in Game Bar");
            return _last;
        }

        // The range the library asked for stays, with the widget's power limit as its top when one is chosen.
        var auto = target == 0 ? null
            : old.AutoSettings is { } kept ? kept with { TargetFps = target }
            : Settings(new AutoTdpRequest(AutoTdpProtocol.Update, target));
        var settings = auto is null ? null : PowerLimits.WithTop(auto, power);

        // A running controller is kept while its settings are the same, so what it has learnt is not lost.
        var controller = settings is null ? null
            : old.Controller is { } running && old.Settings == settings ? running
            : new AutoTdpController(settings, Math.Clamp(WattsNow(old) ?? (settings.MinWatts + settings.MaxWatts + 1) / 2,
                settings.MinWatts, settings.MaxWatts));

        _control = old with { Settings = settings, Controller = controller };
        _control.AutoSettings = auto;
        _control.PowerLimit = power;
        _control.FixedWatts = fixedWatts;
        ChooseFans(_control, fans, _power.ReadTemperature());
        _control.LastFrames = old.LastFrames;

        // Letting go of the power limit or the fans means setting the mode again, which puts back its own of
        // both, so whatever is still wanted is set again straight after.
        if ((WattsNow(old) is not null && WattsNow(_control) is null) || (old.Curve is not null && _control.Curve is null))
        {
            _power.SetVendorMode(old.RestoreMode ?? _power.DefaultVendorMode);
        }

        Set(_control);
        Log.Write($"{Describe(_control)}, changed in Game Bar");
        _last = Status(_control, null, _power.ReadTemperature());
        return _last;
    }

    private void Set(Control control)
    {
        if (WattsNow(control) is { } watts)
        {
            Apply(control, watts);
        }

        if (control.Curve is not null)
        {
            ApplyFans(control);
        }
    }

    /// <summary>The widget's power limit, kept to what the Ally allows, or 0 for Auto.</summary>
    private static int PowerLimit(AutoTdpRequest request) =>
        request.PowerLimit > 0 ? Math.Clamp(request.PowerLimit, AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts) : 0;

    private static AutoTdpSettings? Settings(AutoTdpRequest request)
    {
        if (request.TargetFps <= 0)
        {
            return null;
        }

        var min = Math.Clamp(request.MinWatts > 0 ? request.MinWatts : AutoTdpProtocol.LowestWatts,
            AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts);
        var max = Math.Clamp(request.MaxWatts > 0 ? request.MaxWatts : AutoTdpProtocol.DefaultMaxWatts,
            min, AutoTdpProtocol.HighestWatts);
        return new AutoTdpSettings(
            Math.Clamp(request.TargetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps),
            min,
            max,
            Math.Clamp(request.TemperatureLimit > 0 ? request.TemperatureLimit : AutoTdpProtocol.DefaultTemperature,
                AutoTdpProtocol.LowestTemperature, AutoTdpProtocol.HighestTemperature));
    }

    private static string? FanCurve(string? name) => AutoTdpProtocol.FanModes.Contains(name) ? name : null;

    private static string Describe(Control control)
    {
        var parts = new List<string>();
        if (control.Settings is { } settings)
        {
            parts.Add($"Holding {settings.TargetFps} fps between {settings.MinWatts} and {settings.MaxWatts} W, under {settings.TemperatureLimit} °C, from {control.Controller!.Watts} W");
        }
        else if (control.FixedWatts is { } fixedWatts)
        {
            parts.Add($"Fixed at {fixedWatts} W");
        }

        if (control.DynamicPick is { } pick)
        {
            parts.Add($"dynamic fans, on {pick}");
        }
        else if (control.FanCurve is { } fans)
        {
            parts.Add($"{fans} fan curve");
        }

        return string.Join(", ", parts);
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

        // Setting the mode again brings back its own power limits. When it could not be read, the maker's own
        // default (Performance, on the Ally) is put back instead.
        var mode = control.RestoreMode ?? _power!.DefaultVendorMode;
        var restored = _power!.SetVendorMode(mode);
        MarkHolding(null);
        Log.Write($"Stopped, as {why}. Operating mode {mode} {(restored ? "put back" : "was refused")}");
    }

    /// <summary>
    /// The library's game folders, read from the settings of the player the widget runs as. Read there
    /// rather than from a shared file, so no other account can say what counts as a game.
    /// </summary>
    /// <returns>The folders, or null when the library has not saved any or they cannot be read.</returns>
    private static string[]? ClientGameFolders(int processId)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                // The widget's token is an app container's, but its user is still the player.
                using var identity = new WindowsIdentity(token);
                using var key = Registry.Users.OpenSubKey($@"{identity.User}\{AutoTdpProtocol.LibrarySettingsKey}");
                return key?.GetValue(AutoTdpProtocol.GameFoldersValue) is string[] folders
                    ? [.. folders.Where(folder => !string.IsNullOrWhiteSpace(folder))]
                    : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            Log.Write($"Could not read the library's game folders: {ex.Message}");
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
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

    /// <summary>
    /// The system and administrators own the pipe; the signed-in player may only talk to it, from the
    /// library or from the Game Bar widget. The widget runs in an app sandbox, at low integrity, so the pipe
    /// lets in that one app package (not every app) and is labelled so a low-integrity client may write.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool first)
    {
        // A client opening it to read and write is checked for creating an instance as well, which generic
        // write includes for a pipe. With several instances allowed, that also lets the player's programs
        // add an instance of their own while one is free, so the library checks that the end it reached
        // belongs to this service's process before it trusts an answer.
        var sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";
        if (WidgetSid() is { } widget)
        {
            sddl += $"(A;;GRGW;;;{widget})S:(ML;;NW;;;LW)";
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, 0))
        {
            throw new System.ComponentModel.Win32Exception();
        }

        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + AutoTdpProtocol.PipeName,
                PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0),
                PipeTypeByte | PipeRejectRemoteClients, PipeInstances, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException($"The pipe could not be created ({new System.ComponentModel.Win32Exception(error).Message})");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>The Game Bar widget's app package as a security identifier, or null when Windows cannot work it out.</summary>
    private static string? WidgetSid()
    {
        if (DeriveAppContainerSidFromAppContainerName(AutoTdpProtocol.WidgetPackage, out var sid) != 0)
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(sid).Value;
        }
        finally
        {
            FreeSid(sid);
        }
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

    /// <param name="Settings">What TDP Engine holds, or null when it only sets the fans.</param>
    /// <param name="RestoreMode">Armoury Crate's operating mode from before, to put back, or null when it could not be read.</param>
    /// <param name="FromWidget">Started from Game Bar, with no library to say when the game closes.</param>
    private sealed record Control(AutoTdpSettings? Settings, AutoTdpController? Controller, string? GameFolder,
        Process? ClientProcess, int? RestoreMode, bool FromWidget)
    {
        public string? FanCurve { get; set; }

        /// <summary>The range the library or the widget's defaults gave, before the widget's power limit tops it.</summary>
        public AutoTdpSettings? AutoSettings { get; set; }

        /// <summary>The widget's power limit, or 0 for Auto. With a target it tops the range; without one it is <see cref="FixedWatts"/>.</summary>
        public int PowerLimit { get; set; }

        /// <summary>A power limit held as it is, with no frame-rate target, or null.</summary>
        public int? FixedWatts { get; set; }

        /// <summary>The curve to set, or null for the Ally's own or when the firmware refused it.</summary>
        public byte[]? Curve { get; set; }

        /// <summary>Looks for the game while there is no folder to follow, as when started from Game Bar.</summary>
        public GameLock? Lock { get; set; }

        /// <summary>
        /// For a session started from Game Bar, the folders of the library's installed games, Apps left out.
        /// Only programs inside one of them count. Null when the library has not saved any.
        /// </summary>
        public string[]? LibraryFolders { get; set; }

        /// <summary>The game's folder once <see cref="Lock"/> found it; followed from then on, as GameFolder is.</summary>
        public string? FoundFolder { get; set; }

        /// <summary>What picks the curve while the fans are on Dynamic, or null for a fixed curve.</summary>
        public DynamicFans? Dynamic { get; set; }

        /// <summary>The fixed curve Dynamic is on now, so a change can be written and logged once.</summary>
        public string? DynamicPick { get; set; }

        public DateTime LastSet { get; set; }
        public DateTime LastFans { get; set; }
        public DateTime LastFrames { get; set; }
    }

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out nint descriptor, nint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);

    private const uint QueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* path, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private const uint TokenQuery = 0x0008;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
