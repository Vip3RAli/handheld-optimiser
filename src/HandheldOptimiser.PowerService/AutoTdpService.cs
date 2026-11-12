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

    // The GPU memory and battery charge limit, set through the same firmware. Made once the firmware is opened.
    private GpuMemoryHost _gpuMemory = new(null);
    private BatteryCareHost _batteryCare = new(null);
    private readonly RadeonHost _radeon = new();

    // Auto or Manual, and the last manual choices, read once as the service starts.
    private readonly TdpEngineSettings _engine = new();
    private FrameCounter? _frames;
    private string? _error;

    // The performance overlay and the stats screen, and what they are told each second: every program's
    // frames in the last second, and the temperature, read only while one of them is on.
    private readonly OverlayHost _overlay = new();
    private readonly ScreenStatsHost _screenStats = new();
    private Dictionary<int, double>? _lastFrames;
    private int? _overlayTemperature;

    // The game being controlled, or null between games.
    private Control? _control;
    private DateTime _idleSince = DateTime.UtcNow;
    private AutoTdpStatus _last = new(false);

    // Set once it has decided to end, so a start that arrives meanwhile is turned away rather than lost.
    private bool _ending;

    // For a session started by itself for a game launched outside the library: the player's library folders,
    // when they were read, and the program being watched until it has held a game's frame rate long enough.
    private string[]? _playerLibrary;
    private DateTime _playerLibraryRead;
    private static readonly TimeSpan PlayerLibraryEvery = TimeSpan.FromMinutes(1);
    private GameLock _runningGameLock = new();

    // Which part of the service answers each request that is not the engine's own. See Routes.
    private readonly Dictionary<string, RequestRoute> _routes;

    public AutoTdpService()
    {
        ServiceName = AutoTdpProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        _routes = Routes();
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
            _batteryCare.RestoreIfEnabled();
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
                AllyControlsStore.SetOemWidget();
                _controls = new AllyControlsHost(ToggleOverlayFromPaddle);
                _controls.Start(cancel);
            }

            _power = PowerBackends.Open();
            _gpuMemory = new GpuMemoryHost(_power as AsusAcpi);
            _batteryCare = new BatteryCareHost(_power as AsusAcpi);
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

            RestoreBoostAfterCrash();

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

                    // While replacing Armoury Crate it runs the controller too, and while the overlay, the stats
                    // screen or the battery charge limit is on it keeps them, so it stays.
                    if (_controls is null && _control is null && !_overlay.Wanted && !_screenStats.On
                        && !_batteryCare.IsSavedOn && DateTime.UtcNow - _idleSince > IdleExit)
                    {
                        _ending = true;
                        Log.Write("Nothing to do, so ending");
                        break;
                    }
                }

                _controls?.RefreshBatteryLighting();
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

    /// <summary>
    /// The last run ended while an Auto session held the processor's boost: the player's own goes back. Auto has
    /// not touched boost since 2026-10-28, so this only finds a note left by an earlier version.
    /// </summary>
    private static void RestoreBoostAfterCrash()
    {
        if (TdpEngineSettings.SavedPlayerBoost() is not { } on)
        {
            return;
        }

        var problem = CpuBoost.Restore(on);
        Log.Write($"The last run ended while Auto held CPU boost. The player's own {(problem is null ? "put back" : $"was refused: {problem}")}");
        TdpEngineSettings.SavePlayerBoost(null);
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

    /// <summary>
    /// In Auto with nothing running, starts a session for a game the player launched outside the library, as
    /// from Epic's own launcher. Only the library and the widget started one before, so the widget showed Auto
    /// on while Fortnite ran with nothing setting its power (2026-11-02). A program counts once it runs from
    /// one of the library's game folders, read through its own user, and holds a game's frame rate for
    /// <see cref="GameLock.HoldFor"/>, so a launcher or a browser never starts one.
    /// </summary>
    private void StartForRunningGame(FrameCounter.FrameRates? frames)
    {
        if (!_engine.Auto || frames is null || _power is null || _error is not null || _ending)
        {
            _runningGameLock = new GameLock();
            return;
        }

        // The library's folders are read through the busiest program's own user, a minute at a time.
        var now = DateTime.UtcNow;
        if (_playerLibrary is null || now - _playerLibraryRead > PlayerLibraryEvery)
        {
            var (_, _, busiest) = GameFps(frames.Rendered, null, null);
            if (busiest > 0)
            {
                _playerLibrary = ClientGameFolders(busiest);
                _playerLibraryRead = now;
            }
        }

        if (_playerLibrary is not { Length: > 0 } library)
        {
            return;
        }

        var (fps, program, processId) = GameFps(frames.Rendered, null, library);
        if (_runningGameLock.Observe(now, program, fps) is null || program is null)
        {
            return;
        }

        _runningGameLock = new GameLock();
        var folder = library.FirstOrDefault(game => GameLock.InFolders(program, [game]));
        Begin(new AutoTdpRequest(AutoTdpProtocol.Update, _engine.AutoTargetFps, GameFolder: folder), processId,
            fromWidget: true, source: ", started outside the library while Auto is on");
    }

    /// <summary>One second: the game's frame rate and the temperature to the controller, and its answer to the firmware.</summary>
    private void Tick()
    {
        var frames = _frames?.Take();

        // The overlay and the stats screen show frames as seen, Fluid Motion Frames included.
        _lastFrames = frames?.Shown;
        if (_overlay.Wanted || _screenStats.On)
        {
            _overlayTemperature = _power?.ReadTemperature();
            _overlay.Keep();
            _screenStats.Keep();
        }

        if (_control is null)
        {
            StartForRunningGame(frames);
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

        // Everything here steers by the frames the game rendered: Fluid Motion Frames roughly doubles what is
        // shown, and steering by that would cut the power to half the frames the player chose. The widget and
        // the Dashboard are still told the shown rate, for the same program.
        var (fps, program, processId) = frames is null ? (0, null, 0)
            : GameFps(frames.Rendered, control.GameFolder ?? control.FoundFolder, control.LibraryFolders);
        var shownFps = frames is not null && frames.Shown.TryGetValue(processId, out var shown) ? shown : fps;
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
                controller.Watts, controller.State, shownFps);
        }
        else if (control.FixedWatts is { } fixedWatts && now - control.LastSet >= Reassert)
        {
            Apply(control, fixedWatts);
        }

        if (control.Auto)
        {
            control = TickAuto(control, now, fps, temperature);
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

        _last = Status(control, shownFps, temperature);
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

    // ----- Auto -----

    /// <summary>
    /// Makes a session an Auto one: fans in fine steps. CPU boost is not Auto's: it stays as the player sets it in
    /// the widget, in Auto as in Manual, which the player asked for on 2026-10-28. The first fan step is picked
    /// here; <see cref="Set"/> writes it.
    /// </summary>
    private static void StartAuto(Control control, bool onBattery, int? temperature)
    {
        control.Auto = true;
        control.OnBattery = onBattery;
        control.FanCurve = AutoTdpProtocol.DynamicFans;
        control.Dynamic = null;
        control.DynamicPick = null;
        control.BoostOn = CpuBoost.Read() ?? false;
        control.Fine = new FineFans();
        control.FineStep = null;
        PickFans(control, DateTime.UtcNow, temperature);
    }

    /// <summary>
    /// A session made Auto, or kept Auto, for this frame rate (0 for none): TDP Engine's range for the power
    /// source, its controller kept where the settings are the same, as <see cref="Update"/> keeps it.
    /// </summary>
    private Control ToAuto(Control old, int targetFps)
    {
        var onBattery = CpuBoost.OnBattery();
        var settings = AutoMode.Settings(targetFps, old.Settings?.TemperatureLimit ?? 0, onBattery);
        var controller = old.Controller is { } running && old.Settings == settings ? running
            : new AutoTdpController(settings, Math.Clamp(WattsNow(old) ?? (settings.MinWatts + settings.MaxWatts + 1) / 2,
                settings.MinWatts, settings.MaxWatts));

        var control = old with { Settings = settings, Controller = controller };
        control.AutoSettings = settings;
        control.PowerLimit = 0;
        control.FixedWatts = null;
        if (!old.Auto)
        {
            StartAuto(control, onBattery, _power!.ReadTemperature());
        }

        return control;
    }

    /// <summary>
    /// One second of an Auto session: the power range follows the power source, and the fans follow the power
    /// and the player's CPU boost.
    /// </summary>
    /// <returns>The session, rebuilt when the power source changed.</returns>
    private Control TickAuto(Control control, DateTime now, double fps, int? temperature)
    {
        var onBattery = CpuBoost.OnBattery();
        if (onBattery != control.OnBattery && control.Settings is { } old && control.Controller is { } running)
        {
            var settings = old with { MaxWatts = AutoMode.TopWatts(onBattery) };
            var from = Math.Clamp(running.Watts, settings.MinWatts, settings.MaxWatts);
            control = control with { Settings = settings, Controller = new AutoTdpController(settings, from) };
            control.AutoSettings = settings;
            control.OnBattery = onBattery;
            _control = control;
            Apply(control, from);
            Log.Write($"Auto: {(onBattery ? "on battery" : "plugged in")}, so up to {settings.MaxWatts} W, from {from} W");
        }

        // Read each second, as the player can switch it in the widget at any time, and the fans run a little
        // harder while it is on.
        var watts = WattsNow(control) ?? 0;
        control.BoostOn = CpuBoost.Read() ?? control.BoostOn;

        if (PickFans(control, now, temperature))
        {
            ApplyFans(control);
            Log.Write($"Auto: fans step {control.FineStep} of {FineFans.Steps}, {temperature?.ToString() ?? "?"} °C, {watts} W, boost {(control.BoostOn ? "on" : "off")}");
        }

        return control;
    }

    /// <summary>Picks the fans' step in Auto, and sets its curve when the step changed.</summary>
    /// <returns>Whether the step changed, so the curve needs writing.</returns>
    private static bool PickFans(Control control, DateTime now, int? temperature)
    {
        if (control.Fine is not { } fine)
        {
            return false;
        }

        var step = fine.Pick(now, temperature, WattsNow(control), TopWatts(control), TemperatureLimit(control), control.BoostOn);
        if (step == control.FineStep)
        {
            return false;
        }

        control.FineStep = step;
        control.Curve = FanCurves.Blend(step);
        return true;
    }

    /// <summary>Ends Auto for a session, as it ends or turns Manual. Boost was never Auto's, so it is left as it is.</summary>
    private static void EndAuto(Control control)
    {
        control.Auto = false;
        control.Fine = null;
        control.FineStep = null;
    }

    /// <summary>
    /// Auto or Manual, from the widget: saved, and applied at once to a running session. Back to Manual, the
    /// session takes the manual choices as the widget would send them, which ends it when there are none.
    /// Called under the gate.
    /// </summary>
    private AutoTdpStatus SetMode(AutoTdpRequest request, int client)
    {
        var auto = AutoMode.IsAuto(request.Mode);
        _idleSince = DateTime.UtcNow;
        if (auto != _engine.Auto)
        {
            _engine.SetMode(auto);
            Log.Write($"Mode set to {(auto ? "Auto" : "Manual")}");

            if (_control is { } control && _power is not null)
            {
                var target = AutoMode.ShownTarget(control.Settings?.TargetFps ?? 0);
                if (auto)
                {
                    _control = ToAuto(control, target);
                    Set(_control);
                    Log.Write($"{Describe(_control)}, changed to Auto in Game Bar");
                    _last = Status(_control, null, _power.ReadTemperature());
                }
                else
                {
                    EndAuto(control);
                    return Update(new AutoTdpRequest(AutoTdpProtocol.Update, target, PowerLimit: _engine.ManualPowerLimit,
                        FanCurve: _engine.ManualFans), client);
                }
            }
        }

        return _error is not null && _control is null ? new AutoTdpStatus(false, Error: _error) : _last;
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
        control.Fine = null;
    }

    private static AutoTdpStatus Status(Control control, double? fps, int? temperature) =>
        new(true, fps, WattsNow(control), temperature,
            control.Controller?.State.ToString() ?? (control.FixedWatts is null ? null : AutoTdpProtocol.FixedState),
            TargetFps: AutoMode.ShownTarget(control.Settings?.TargetFps ?? 0), FanCurve: control.Curve is null ? null : control.FanCurve,
            PowerLimit: control.PowerLimit);

    /// <summary>
    /// The game's frames a second: the busiest program running from its folder. Without a folder, the
    /// busiest program of all but Windows' own compositor, whose frames are every window's.
    /// </summary>
    /// <param name="library">Until the game's folder is known, the library's game folders. See AutoTdpRules.GameFps.</param>
    private static (double Fps, string? Program, int ProcessId) GameFps(Dictionary<int, double> frames, string? folder, string[]? library) =>
        AutoTdpRules.GameFps(frames, folder, library, ProcessPath);

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

        /// <summary>Whether this is an Auto session: TDP Engine's own power range and fans in fine steps.</summary>
        public bool Auto { get; set; }

        /// <summary>What picks the fans' step in Auto, and the step written last, or null before the first.</summary>
        public FineFans? Fine { get; set; }
        public int? FineStep { get; set; }

        /// <summary>Whether the player's CPU boost was on at the last tick, which the fans in Auto allow for.</summary>
        public bool BoostOn { get; set; }

        /// <summary>Whether the power source was the battery at the last tick, so a change of it is seen.</summary>
        public bool OnBattery { get; set; }

        public DateTime LastSet { get; set; }
        public DateTime LastFans { get; set; }
        public DateTime LastFrames { get; set; }
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

    private const uint TokenQuery = 0x0008;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);
}
