using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// A game's session: starting it, changing it and ending it, and the requests about the overlay and the
/// stats screen, which are answered under the gate because they read what the engine holds.
/// </summary>
internal sealed partial class AutoTdpService
{
    /// <summary>A new overlay level, corner or layout, from the widget. Called under the gate.</summary>
    private AutoTdpStatus SetOverlay(AutoTdpRequest request)
    {
        _overlay.Set(request.OverlayLevel, request.OverlayCorner, request.OverlayLayout);
        _idleSince = DateTime.UtcNow;
        return _error is not null && _control is null ? new AutoTdpStatus(false, Error: _error) : _last;
    }

    // The YouTube Music player, started once when the widget asks. It is not kept running: it ends when the
    // player closes it from the widget, and nothing starts it again until they ask.
    private readonly SessionProgram _music = new("Music player", AutoTdpProtocol.Music.Argument, AutoTdpProtocol.Music.MutexName, uiAccess: false);

    /// <summary>Starts the YouTube Music player in the player's session, unless it is running.</summary>
    private AutoTdpStatus StartMusic(AutoTdpStatus last)
    {
        _music.TryNow();
        _music.Keep();
        return last;
    }

    /// <summary>The stats screen switched on or off, from the widget. Called under the gate.</summary>
    private AutoTdpStatus SetScreenStats(AutoTdpRequest request)
    {
        if (request.ScreenStats is { } on)
        {
            _screenStats.Set(on);
        }

        if (request.ScreenStatsLime is { } lime)
        {
            _screenStats.SetLime(lime);
        }

        _idleSince = DateTime.UtcNow;
        return _error is not null && _control is null ? new AutoTdpStatus(false, Error: _error) : _last;
    }

    /// <summary>
    /// What the overlay and the stats screen show from here, each second: the frame rate, the power limit
    /// TDP Engine holds (none when it holds none), and the temperature. While a session runs the frame rate
    /// is the one the widget shows, the program TDP Engine follows, so the three always agree; otherwise it
    /// is the program in front's. Called under the gate.
    /// </summary>
    private AutoTdpStatus OverlayStatus(AutoTdpRequest request)
    {
        double? fps = _control is not null && _last is { Running: true, Fps: { } followed } ? followed
            : _lastFrames is { } frames ? AutoTdpRules.ProgramFps(frames, request.ProcessId, ProcessPath)
            : null;
        int? watts = _control is { } control ? WattsNow(control) : null;
        return _last with { Fps = fps, Watts = watts, Temperature = _overlayTemperature };
    }

    private void ToggleOverlayFromPaddle()
    {
        lock (_gate)
        {
            _overlay.Toggle();
            _idleSince = DateTime.UtcNow;
        }
    }

    /// <summary>A game starting from the library, or from the widget when nothing is running.</summary>
    /// <param name="source">How it started, for the log, when neither the library nor the widget.</param>
    private AutoTdpStatus Begin(AutoTdpRequest request, int client, bool fromWidget = false, string? source = null)
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

        // In Auto, TDP Engine's own range and fans, whatever the library's profile or the widget named, and a
        // frame rate is optional. In Manual, what the widget chose is kept for next time.
        var onBattery = CpuBoost.OnBattery();
        if (_engine.Auto)
        {
            auto = AutoMode.Settings(request.TargetFps, request.TemperatureLimit, onBattery);
            settings = auto;
            power = 0;
            fixedWatts = null;
            fans = AutoTdpProtocol.DynamicFans;
        }
        else if (fromWidget)
        {
            _engine.SaveManual(power, fans);
        }

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

        // A new game keeps the operating mode saved for the first, which is the player's own, and in Auto the
        // player's own boost, as the first may have changed it since.
        var previous = _control;
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

        if (_engine.Auto)
        {
            StartAuto(_control, onBattery, _power.ReadTemperature());
        }
        else
        {
            if (previous is { Auto: true })
            {
                EndAuto(previous);
            }

            ChooseFans(_control, fans, _power.ReadTemperature());
        }

        // Saved before the limit is first set, so a crash from here on still gets the mode put back.
        MarkHolding(restoreMode ?? _power.DefaultVendorMode);
        Set(_control);
        var target = (folder is null ? null : AutoTdpRules.Printable(folder))
            ?? (_control.LibraryFolders is { } library ? $"the busiest of the library's {library.Length} games" : "the busiest program");
        Log.Write($"{Describe(_control)}, for {target}{source ?? (fromWidget ? ", from Game Bar" : "")}");
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

        // In Auto only the frame rate is the widget's, and none is a target too, not off.
        if (_engine.Auto)
        {
            _control = ToAuto(old, request.TargetFps);
            Set(_control);
            Log.Write($"{Describe(_control)}, changed in Game Bar");
            _last = Status(_control, null, _power.ReadTemperature());
            return _last;
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

        _engine.SaveManual(power, fans);

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
        // Kept for a session started for a game launched outside the library (see StartForRunningGame).
        if (control.Auto)
        {
            _engine.SaveAutoTarget(AutoMode.ShownTarget(control.Settings?.TargetFps ?? 0));
        }

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
            var aim = settings.TargetFps == AutoTdpProtocol.NoTargetFps ? "Most frames for the power" : $"Holding {settings.TargetFps} fps";
            parts.Add($"{(control.Auto ? "Auto: " : string.Empty)}{aim} between {settings.MinWatts} and {settings.MaxWatts} W, under {settings.TemperatureLimit} °C, from {control.Controller!.Watts} W");
        }
        else if (control.FixedWatts is { } fixedWatts)
        {
            parts.Add($"Fixed at {fixedWatts} W");
        }

        if (control.FineStep is { } step)
        {
            parts.Add($"fans step {step} of {FineFans.Steps}, boost {(control.BoostOn ? "on" : "off")}");
        }
        else if (control.DynamicPick is { } pick)
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
        if (control.Auto)
        {
            EndAuto(control);
        }

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
}
