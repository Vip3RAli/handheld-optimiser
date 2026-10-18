using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// The switch that hands the ROG Ally's Armoury Crate and Command Center buttons and its controller mode to the
/// app's own TDP Engine service, and turns ASUS's background services off. Only ASUS's services, one scheduled task
/// and one startup entry are touched. ASUS's drivers (ATKWMIACPIIO, AsusSAIO and their devices), which TDP Engine
/// needs, and the Armoury Crate SE and Command Center packages are never named here.
///
/// Start types go through sc.exe, as in <see cref="ServiceTweaks"/>, so the change takes effect at once and a revert
/// can start the services again without a restart. Every original value goes into the undo journal first.
/// </summary>
public static class ArmouryCrateTweaks
{
    private const string Id = "asus.replace-armoury-crate";
    private const string Name = "Replace Armoury Crate";

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>
    /// Where TDP Engine reads whether it stands in for Armoury Crate. The main app does not build in the service's
    /// protocol file, so these are its own copies of <c>AutoTdpProtocol.ControlsKey</c> and
    /// <c>AutoTdpProtocol.ReplacingValue</c> in src\HandheldOptimiser.PowerService\AutoTdpProtocol.cs, and the two
    /// must be kept in step.
    /// </summary>
    public const string ControlsKey = @"SOFTWARE\HandheldOptimiser\AllyControls";

    /// <inheritdoc cref="ControlsKey"/>
    public const string ReplacingValue = "Replacing";

    /// <summary>The TDP Engine service, the same name as <c>AutoTdpProtocol.ServiceName</c>.</summary>
    public const string EngineService = "HandheldOptimiserAutoTdp";

    /// <summary>The Ally's controller, the only device this switch is for.</summary>
    public const string AllyController = "VID_0B05&PID_1ABE";

    /// <summary>
    /// ASUS's services, in the order they are stopped. ASUS App Service is the watchdog that starts the others
    /// again, so it goes first, and the Armoury Crate SE service goes last. installer\HandheldOptimiser.iss
    /// repeats this list for the uninstaller, so a change here is made there too.
    /// </summary>
    public static IReadOnlyList<string> ServicesInStopOrder { get; } =
    [
        "AsusAppService",
        "ASUSOptimization",
        "ASUSSoftwareManager",
        "ASUSSwitch",
        "ASUSSystemAnalysis",
        "ASUSSystemDiagnosis",
        "ArmouryCrateControlInterface",
        "ROG Live Service",
        "GameSDK Service",
        "LightingService",
        "ArmouryCrateSEService"
    ];

    /// <summary>
    /// The two ASUS Update services. They are set Disabled but not stopped, since Manual is not enough: Armoury
    /// Crate's app can start a Manual service. installer\HandheldOptimiser.iss repeats this list as well.
    /// </summary>
    public static IReadOnlyList<string> UpdateServices { get; } = ["asus", "asusm"];

    /// <summary>ASUS programs that may still be running once the services stop, closed by name.</summary>
    public static IReadOnlyList<string> ProcessesToClose { get; } =
    [
        "ArmouryCrateSE.UserSessionHelper",
        "CommandCenterOsd",
        "CommandCenter",
        "asus_framework",
        "ROGScreenManager",
        "AsusOptimizationStartupTask",
        "AsusSoftwareManagerAgent"
    ];

    /// <summary>The scheduled task that starts asus_framework at sign-in.</summary>
    public const string FrameworkTask = @"\ASUS\Framework Service";

    /// <summary>The 32-bit Run entry that starts ROG Screen Manager.</summary>
    public const string ScreenManagerEntry = "ROG Screen Manager";

    private const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Task Manager keeps the on and off flags for 32-bit Run entries in the 64-bit view under Run32, and the entry
    /// is turned off there, as Task Manager does, rather than deleted.
    /// </summary>
    private const string ApprovedKey32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    /// <summary>
    /// The flag Task Manager writes for an entry the user turned off: 03 first, the rest zero. Twelve bytes, as
    /// <see cref="StartupService"/> writes it and as Task Manager does.
    /// </summary>
    private static readonly byte[] DisabledFlag = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The journal's word for a Run32 value that was not there, so revert removes it again.</summary>
    private const string Absent = "absent";

    private const string NotSupported = "This needs the original ROG Ally and TDP Engine installed.";

    public static IReadOnlyList<Tweak> All => [ReplaceArmouryCrate];

    public static Tweak ReplaceArmouryCrate { get; } = new()
    {
        Id = Id,
        Name = Name,
        Description =
            "Turns off Armoury Crate and ASUS's background services. Handheld Optimiser runs the Armoury Crate and " +
            "Command Center buttons and the controller mode instead.",
        Category = TweakCategory.Services,
        Risk = RiskLevel.Moderate,
        Warning =
            "Armoury Crate stops working while this is on. ASUS BIOS and controller updates come through Armoury " +
            "Crate, so turn this off to update.",
        OnlyFor = HandheldDevice.RogAlly,
        IncludeInOneClick = false,

        // Detection reads the registry only, which sc.exe keeps in step, so the page starts no PowerShell for it.
        ScriptDetectIsQuick = true,
        ScriptDetect = (ctx, _) => Task.FromResult(Detect(ctx.Registry)),
        ScriptApply = ApplyAsync,
        ScriptRevert = RevertAsync
    };

    /// <summary>
    /// NotApplied unless TDP Engine is told to replace Armoury Crate. Then Applied when every listed service this
    /// Ally has is Disabled, and Partial otherwise, as after an Armoury Crate update turns one back on. A service
    /// ASUS did not install is skipped.
    /// </summary>
    private static TweakState Detect(IRegistryStore registry)
    {
        if (registry.ReadValue(RegistryRoot.LocalMachine, ControlsKey, ReplacingValue) is not 1)
        {
            return TweakState.NotApplied;
        }

        var allDisabled = ServicesInStopOrder
            .Select(s => registry.ReadValue(RegistryRoot.LocalMachine, $@"{ServicesKey}\{s}", "Start"))
            .OfType<int>()
            .All(start => start == 4);

        return allDisabled ? TweakState.Applied : TweakState.Partial;
    }

    private static async Task<TweakResult> ApplyAsync(TweakContext ctx, TweakJournalEntry journal, CancellationToken ct)
    {
        foreach (var service in ServicesInStopOrder.Concat(UpdateServices))
        {
            if (SafetyGuard.IsServiceProtected(service, out var reason))
            {
                ctx.Log.Error($"BLOCKED service {service}: {reason}");
                return TweakResult.Blocked(Id, reason!);
            }
        }

        // Checked before anything is journalled, so a device this is not for keeps no undo data.
        var check = await ctx.Runner.RunScriptAsync(BuildCheckScript(), "Check for the ROG Ally controller and TDP Engine", ct);
        if (!check.Succeeded)
        {
            return TweakResult.Fail(Id, "Could not check for the ROG Ally controller and TDP Engine. See log.");
        }

        var lines = check.OutputLines.ToList();
        if (!lines.Contains("DEVICE=1") || !lines.Contains("ENGINE=1"))
        {
            ctx.Log.Warning($"\"{Name}\": {NotSupported}");
            return TweakResult.Fail(Id, NotSupported);
        }

        // Every service this Ally has is journalled, whatever its start type, so revert puts back exactly what
        // was found. The journal keeps the first original on a second apply, so an Armoury Crate update that
        // turned a service back on between the two does not overwrite it.
        var present = new List<string>();
        foreach (var service in ServicesInStopOrder.Concat(UpdateServices))
        {
            var key = $@"{ServicesKey}\{service}";
            if (ctx.Registry.ReadValue(RegistryRoot.LocalMachine, key, "Start") is not int start)
            {
                ctx.Log.Trace($"    {service}: not installed on this system, skipping");
                continue;
            }

            var delayed = ctx.Registry.ReadValue(RegistryRoot.LocalMachine, key, "DelayedAutostart");
            journal.CapturedState.TryAdd($"service.{service}.start", start.ToString());
            journal.CapturedState.TryAdd($"service.{service}.delayed", delayed is 1 ? "1" : "0");
            present.Add(service);
        }

        var taskState = lines.FirstOrDefault(l => l.StartsWith("TASK=", StringComparison.Ordinal))?["TASK=".Length..];
        if (taskState is { Length: > 0 })
        {
            journal.CapturedState.TryAdd("task.framework", taskState);
        }

        var turnOffRunEntry = false;
        if (ctx.Registry.ReadValue(RegistryRoot.LocalMachine, RunKey32, ScreenManagerEntry) is not null)
        {
            switch (ctx.Registry.ReadValue(RegistryRoot.LocalMachine, ApprovedKey32, ScreenManagerEntry))
            {
                case null:
                    journal.CapturedState.TryAdd("run.screenmanager", Absent);
                    turnOffRunEntry = true;
                    break;
                case byte[] bytes:
                    journal.CapturedState.TryAdd("run.screenmanager", Convert.ToHexString(bytes));
                    turnOffRunEntry = true;
                    break;
                default:
                    // Not a flag Task Manager wrote, so it could not be put back exactly. Left alone.
                    ctx.Log.Warning($"    The {ScreenManagerEntry} startup flag is not binary, leaving it as it is");
                    break;
            }
        }

        var stop = ServicesInStopOrder.Where(present.Contains).ToList();

        var outcome = await ctx.Runner.RunScriptAsync(
            BuildApplyScript(present, stop, disableTask: taskState is { Length: > 0 }, turnOffRunEntry),
            $"Replace Armoury Crate: disable {string.Join(", ", present)}",
            ct);

        return outcome.Succeeded
            ? TweakResult.Ok(Id, $"\"{Name}\" applied.")
            : TweakResult.Fail(Id, "Armoury Crate could not be fully replaced. See log.");
    }

    private static async Task<TweakResult> RevertAsync(TweakContext ctx, TweakJournalEntry journal, CancellationToken ct)
    {
        foreach (var service in ServicesInStopOrder.Concat(UpdateServices))
        {
            if (journal.CapturedState.ContainsKey($"service.{service}.start") &&
                SafetyGuard.IsServiceProtected(service, out var reason))
            {
                ctx.Log.Error($"BLOCKED service {service}: {reason}");
                return TweakResult.Blocked(Id, reason!);
            }
        }

        var outcome = await ctx.Runner.RunScriptAsync(
            BuildRevertScript(journal.CapturedState),
            $"Restore Armoury Crate for \"{Name}\"",
            ct);

        return outcome.Succeeded
            ? TweakResult.Ok(Id, $"\"{Name}\" reverted.")
            : TweakResult.Fail(Id, "Armoury Crate could not be fully restored. See log.");
    }

    /// <summary>
    /// Prints DEVICE=1 when the Ally's controller is present, ENGINE=1 when the TDP Engine service is installed,
    /// and TASK= with the framework task's state when the task exists. Changes nothing.
    /// </summary>
    public static string BuildCheckScript() => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $pad = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like {{Quote($"*{AllyController}*")}} })
        Write-Output "DEVICE=$(if ($pad.Count -gt 0) { 1 } else { 0 })"
        Write-Output "ENGINE=$(if (Get-Service -Name {{Quote(EngineService)}}) { 1 } else { 0 })"
        $task = Get-ScheduledTask -TaskPath {{Quote(TaskFolder)}} -TaskName {{Quote(TaskLeaf)}}
        if ($task) { Write-Output "TASK=$($task.State)" }
        """;

    /// <summary>
    /// The script that turns Armoury Crate off. Every service in <paramref name="disable"/> is set Disabled first,
    /// so that nothing (the watchdog or Armoury Crate's app) can start one again while the rest are stopped, then
    /// those in <paramref name="stop"/> are stopped in order. TDP Engine is told it is replacing Armoury Crate
    /// before it is started, since it reads that as it starts, and restarted if it was already running.
    /// A service that will not stop, or a running process that will not close, is counted as a failure but does not
    /// end the script, so one stubborn service does not leave the rest half done. Processes are walked with a plain
    /// foreach rather than ForEach-Object, whose script block would set its own copy of $failed.
    /// </summary>
    public static string BuildApplyScript(
        IReadOnlyList<string> disable,
        IReadOnlyList<string> stop,
        bool disableTask,
        bool turnOffRunEntry)
    {
        var task = disableTask
            ? $$"""
                Write-Output "Disabling scheduled task: {{FrameworkTask}}"
                try { Disable-ScheduledTask -TaskPath {{Quote(TaskFolder)}} -TaskName {{Quote(TaskLeaf)}} -ErrorAction Stop | Out-Null }
                catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                """
            : "Write-Output \"No scheduled task to disable\"";

        var run = turnOffRunEntry
            ? $$"""
                Write-Output "Turning off the startup entry: {{ScreenManagerEntry}}"
                try {
                    $k = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey({{Quote(ApprovedKey32)}})
                    $k.SetValue({{Quote(ScreenManagerEntry)}}, {{ByteLiteral(DisabledFlag)}}, [Microsoft.Win32.RegistryValueKind]::Binary)
                    $k.Close()
                } catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                """
            : "Write-Output \"No startup entry to turn off\"";

        return $$"""
            $ErrorActionPreference = 'Continue'
            $failed = $false
            foreach ($n in @({{List(disable)}})) {
                Write-Output "Setting $n start type to Disabled"
                & sc.exe config $n start= disabled | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Output "sc.exe config failed for $n (exit $LASTEXITCODE)"; $failed = $true }
            }
            foreach ($n in @({{List(stop)}})) {
                $s = Get-Service -Name $n -ErrorAction SilentlyContinue
                if ($s -and $s.Status -ne 'Stopped') {
                    Write-Output "Stopping $n"
                    try { Stop-Service -Name $n -Force -ErrorAction Stop }
                    catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                }
            }
            foreach ($p in @({{List(ProcessesToClose)}})) {
                foreach ($proc in @(Get-Process -Name $p -ErrorAction SilentlyContinue)) {
                    Write-Output "Closing $($proc.ProcessName) ($($proc.Id))"
                    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop }
                    catch {
                        if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                        else { Write-Output "  it had already closed" }
                    }
                }
            }
            {{task}}
            {{run}}
            Write-Output "Telling TDP Engine to replace Armoury Crate"
            try {
                $k = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey({{Quote(ControlsKey)}})
                $k.SetValue({{Quote(ReplacingValue)}}, 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                $k.Close()
            } catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
            & sc.exe config {{EngineService}} start= auto | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Output "sc.exe config failed for {{EngineService}} (exit $LASTEXITCODE)"; $failed = $true }
            & sc.exe failure {{EngineService}} reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Output "sc.exe failure failed for {{EngineService}} (exit $LASTEXITCODE)"; $failed = $true }
            $e = Get-Service -Name {{Quote(EngineService)}} -ErrorAction SilentlyContinue
            try {
                if ($e -and $e.Status -eq 'Running') {
                    Write-Output "Restarting {{EngineService}}"
                    Restart-Service -Name {{Quote(EngineService)}} -Force -ErrorAction Stop
                } else {
                    Write-Output "Starting {{EngineService}}"
                    Start-Service -Name {{Quote(EngineService)}} -ErrorAction Stop
                }
            } catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
            if ($failed) { exit 1 }
            """;
    }

    /// <summary>
    /// The script that puts back what <see cref="BuildApplyScript"/> changed, from the journal. TDP Engine lets go
    /// of the controller first (Replacing 0, Manual, restarted if running), then each ASUS service gets its start
    /// type back, the task and the startup flag are put back, and the services that were Automatic are started,
    /// the Armoury Crate SE service last. Only names from this class's own lists are ever acted on, whatever else
    /// the journal holds.
    /// </summary>
    public static string BuildRevertScript(IReadOnlyDictionary<string, string> captured)
    {
        var restore = new List<string>();
        var start = new List<string>();

        // The update services first and the stop order after, so that ArmouryCrateSEService starts last.
        foreach (var service in UpdateServices.Concat(ServicesInStopOrder))
        {
            if (!captured.TryGetValue($"service.{service}.start", out var startType))
            {
                continue;
            }

            captured.TryGetValue($"service.{service}.delayed", out var delayed);

            var scStart = startType switch
            {
                "2" when delayed == "1" => "delayed-auto",
                "2" => "auto",
                "3" => "demand",
                "4" => "disabled",
                _ => "demand"
            };

            restore.Add($"Set-Start {Quote(service)} '{scStart}'");
            if (startType == "2")
            {
                start.Add(service);
            }
        }

        var task = captured.TryGetValue("task.framework", out var taskState) && taskState != "Disabled"
            ? $$"""
                Write-Output "Enabling scheduled task: {{FrameworkTask}}"
                try { Enable-ScheduledTask -TaskPath {{Quote(TaskFolder)}} -TaskName {{Quote(TaskLeaf)}} -ErrorAction Stop | Out-Null }
                catch { Write-Output "  failed: $($_.Exception.Message)"; $script:failed = $true }
                """
            : "Write-Output \"No scheduled task to enable\"";

        var run = "Write-Output \"No startup entry to put back\"";
        if (captured.TryGetValue("run.screenmanager", out var flag))
        {
            var put = flag == Absent
                ? $"$k.DeleteValue({Quote(ScreenManagerEntry)}, $false)"
                : $"$k.SetValue({Quote(ScreenManagerEntry)}, {ByteLiteral(Convert.FromHexString(flag))}, [Microsoft.Win32.RegistryValueKind]::Binary)";

            run = $$"""
                Write-Output "Putting back the startup entry: {{ScreenManagerEntry}}"
                try {
                    $k = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey({{Quote(ApprovedKey32)}})
                    {{put}}
                    $k.Close()
                } catch { Write-Output "  failed: $($_.Exception.Message)"; $script:failed = $true }
                """;
        }

        return $$"""
            $ErrorActionPreference = 'Continue'
            $script:failed = $false
            Write-Output "Telling TDP Engine to stop replacing Armoury Crate"
            try {
                $k = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey({{Quote(ControlsKey)}})
                $k.SetValue({{Quote(ReplacingValue)}}, 0, [Microsoft.Win32.RegistryValueKind]::DWord)
                $k.Close()
            } catch { Write-Output "  failed: $($_.Exception.Message)"; $script:failed = $true }
            $e = Get-Service -Name {{Quote(EngineService)}} -ErrorAction SilentlyContinue
            if ($e) {
                & sc.exe config {{EngineService}} start= demand | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Output "sc.exe config failed for {{EngineService}} (exit $LASTEXITCODE)"; $script:failed = $true }
                if ($e.Status -eq 'Running') {
                    Write-Output "Restarting {{EngineService}} so it lets go of the controller"
                    try { Restart-Service -Name {{Quote(EngineService)}} -Force -ErrorAction Stop }
                    catch { Write-Output "  failed: $($_.Exception.Message)"; $script:failed = $true }
                }
            } else {
                Write-Output "{{EngineService}} is not installed, nothing to put back for it"
            }
            function Set-Start($n, $start) {
                Write-Output "Setting $n start type to $start"
                & sc.exe config $n start= $start | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Output "sc.exe config failed for $n (exit $LASTEXITCODE)"; $script:failed = $true }
            }
            {{string.Join("\n", restore)}}
            {{task}}
            {{run}}
            foreach ($n in @({{List(start)}})) {
                Write-Output "Starting $n"
                try { Start-Service -Name $n -ErrorAction Stop }
                catch { Write-Output "  failed: $($_.Exception.Message)"; $script:failed = $true }
            }
            if ($script:failed) { exit 1 }
            """;
    }

    private static string TaskFolder => FrameworkTask[..(FrameworkTask.LastIndexOf('\\') + 1)];

    private static string TaskLeaf => FrameworkTask[(FrameworkTask.LastIndexOf('\\') + 1)..];

    /// <summary>A PowerShell single quoted string, which takes spaces and needs only its own quote doubled.</summary>
    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string List(IEnumerable<string> values) => string.Join(", ", values.Select(Quote));

    private static string ByteLiteral(byte[] bytes) =>
        $"[byte[]]@({string.Join(", ", bytes.Select(b => $"0x{b:X2}"))})";
}
