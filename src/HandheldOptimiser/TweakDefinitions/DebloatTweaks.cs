using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Telemetry, search, assistant and background-app tweaks. Nothing here touches Defender, the firewall,
/// Windows Update or any vendor software. See <see cref="Services.SafetyGuard"/>, which enforces that at
/// write time rather than trusting this file to stay well-behaved.
/// </summary>
public static class DebloatTweaks
{
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string SearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    private const string CloudContent = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";

    public static IReadOnlyList<Tweak> All =>
    [
        Telemetry,
        WebSearch,
        CortanaAndCopilot,
        ConsumerFeatures,
        ShellAds,
        Widgets,
        EdgeBackground,
        ActivityHistory,
        ErrorReporting,
        OneDrive,
        BackgroundApps
    ];

    /// <summary>
    /// The scheduled tasks that feed the Compatibility Appraiser and CEIP pipelines, plus the two
    /// telemetry services. Windows Update is entirely independent of these.
    /// </summary>
    private static readonly string[] TelemetryTasks =
    [
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Application Experience\StartupAppTask",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"
    ];

    public static Tweak Telemetry => new()
    {
        Id = "debloat.telemetry",
        Name = "Disable telemetry & data collection",
        Description =
            "Sets telemetry to the minimum the OS allows, disables the Compatibility Appraiser and CEIP " +
            "scheduled tasks, turns off the advertising ID, and stops the DiagTrack service. Windows Update " +
            "and Defender are unaffected, as neither depends on any of this.",
        Category = TweakCategory.Privacy,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord),

            // Service start types. 4 = disabled, and the snapshot captures the original so revert is exact.
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Services\dmwappushservice", "Start", 4, RegistryValueKind.DWord)
        ],
        ScriptDetect = async (ctx, ct) =>
        {
            var outcome = await ctx.Runner.RunScriptAsync(
                BuildTaskStateScript(),
                "Check telemetry scheduled task states",
                ct,
                echoScript: false);

            var states = outcome.OutputLines
                .Where(l => l.StartsWith("TASK=", StringComparison.Ordinal))
                .Select(l => l.Split('=', 3))
                .Where(p => p.Length == 3)
                .Select(p => p[2])
                .ToList();

            if (states.Count == 0)
            {
                return TweakState.Unknown;
            }

            var disabled = states.Count(s => s.Equals("Disabled", StringComparison.OrdinalIgnoreCase));

            return disabled == states.Count ? TweakState.Applied
                : disabled == 0 ? TweakState.NotApplied
                : TweakState.Partial;
        },
        ScriptApply = async (ctx, journal, ct) =>
        {
            var capture = await ctx.Runner.RunScriptAsync(
                BuildTaskStateScript(),
                "Capture telemetry task states before changing them",
                ct,
                echoScript: false);

            foreach (var line in capture.OutputLines.Where(l => l.StartsWith("TASK=", StringComparison.Ordinal)))
            {
                var parts = line.Split('=', 3);
                if (parts.Length == 3)
                {
                    journal.CapturedState[$"task.{parts[1]}"] = parts[2];
                }
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                BuildTaskChangeScript(TelemetryTasks, disable: true),
                "Disable telemetry scheduled tasks",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("debloat.telemetry", "Telemetry tasks disabled.", rebootRequired: true)
                : TweakResult.Fail("debloat.telemetry", "One or more telemetry tasks could not be disabled. See log.");
        },
        ScriptRevert = async (ctx, journal, ct) =>
        {
            // A task captured as Running or Queued was enabled too; only Disabled ones stay off. Names come
            // from the task list rather than the journal's keys, because the journal is a file on disk and
            // these names are pasted into an elevated script.
            var toEnable = TelemetryTasks
                .Where(t => journal.CapturedState.TryGetValue($"task.{t}", out var state) &&
                            !state.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (toEnable.Length == 0)
            {
                ctx.Log.Info("No telemetry tasks were enabled before; nothing to re-enable.");
                return TweakResult.NoChange("debloat.telemetry");
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                BuildTaskChangeScript(toEnable, disable: false),
                "Re-enable telemetry scheduled tasks",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("debloat.telemetry", "Telemetry tasks restored.")
                : TweakResult.Fail("debloat.telemetry", "Some telemetry tasks could not be re-enabled. See log.");
        }
    };

    public static Tweak WebSearch => new()
    {
        Id = "debloat.websearch",
        Name = "Disable web search in Start menu",
        Description =
            "Start menu search stops reaching out to Bing and only returns local results. Makes search " +
            "instant and stops the Start menu hanging when Wi-Fi is flaky.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, SearchPolicy, "DisableWebSearch", 1, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, SearchPolicy, "ConnectedSearchUseWeb", 0, RegistryValueKind.DWord)
        ]
    };

    public static Tweak CortanaAndCopilot => new()
    {
        Id = "debloat.cortana_copilot",
        Name = "Disable Cortana & Copilot",
        Description =
            "Turns off Cortana and Windows Copilot by policy and removes the Copilot button from the " +
            "taskbar, so neither runs background tasks or consumes memory.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, SearchPolicy, "AllowCortana", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, RegistryValueKind.DWord)
        ]
    };

    public static Tweak ConsumerFeatures => new()
    {
        Id = "debloat.consumer_features",
        Name = "Stop app suggestions & silent reinstalls",
        Description =
            "Prevents Windows from silently installing promoted apps, showing suggestions in Start, and " +
            "re-adding the bloatware you remove. Worth applying before the bloatware removal so nothing " +
            "comes back.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, ContentDelivery, "SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SystemPaneSuggestionsEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "PreInstalledAppsEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "OemPreInstalledAppsEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-338388Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-338389Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-353698Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "FeatureManagementEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, CloudContent, "DisableWindowsConsumerFeatures", 1, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, CloudContent, "DisableCloudOptimizedContent", 1, RegistryValueKind.DWord)
        ]
    };

    public static Tweak ShellAds => new()
    {
        Id = "debloat.shell_ads",
        Name = "Hide ads in Start, Settings and File Explorer",
        Description =
            "Turns off app recommendations and Microsoft account nags in Start, suggested content in Settings, " +
            "OneDrive prompts in File Explorer, tips on the lock screen, and the \"finish setting up your " +
            "device\" screen that appears after updates.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, ExplorerAdvanced, "Start_IrisRecommendations", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ExplorerAdvanced, "Start_AccountNotifications", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ExplorerAdvanced, "ShowSyncProviderNotifications", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-338393Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-353694Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-353696Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-310093Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "SubscribedContent-338387Enabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, ContentDelivery, "RotatingLockScreenOverlayEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement",
                "ScoobeSystemSettingEnabled", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// The policy rather than the taskbar's TaskbarDa value: on current builds a driver blocks other
    /// programs from writing TaskbarDa, and the policy also stops the board's host process starting.
    /// </summary>
    public static Tweak Widgets => new()
    {
        Id = "debloat.widgets",
        Name = "Turn off Widgets",
        Description =
            "Removes the Widgets board and its taskbar button. The board keeps a set of Edge WebView " +
            "processes running in the background, typically 100 to 200 MB of memory the game could use.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, RegistryValueKind.DWord)
        ]
    };

    public static Tweak EdgeBackground => new()
    {
        Id = "debloat.edge_background",
        Name = "Stop Edge running in the background",
        Description =
            "Turns off Edge's Startup Boost, which preloads it at sign-in, and stops it staying open in the " +
            "background after you close the last window. Frees the memory those hidden Edge processes hold.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        Warning =
            "Edge takes a moment longer to open, and its settings page says some settings are managed by your " +
            "organisation. That note is these two policies, nothing else.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, EdgePolicy, "StartupBoostEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, EdgePolicy, "BackgroundModeEnabled", 0, RegistryValueKind.DWord)
        ]
    };

    public static Tweak ActivityHistory => new()
    {
        Id = "debloat.activity_history",
        Name = "Turn off activity history & tailored experiences",
        Description =
            "Stops Windows recording which apps and files you open, and stops it using diagnostic data to " +
            "personalise tips and offers. Less background logging, and nothing you use goes away.",
        Category = TweakCategory.Privacy,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, SystemPolicy, "EnableActivityFeed", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, SystemPolicy, "PublishUserActivities", 0, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, SystemPolicy, "UploadUserActivities", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent",
                "DisableTailoredExperiencesWithDiagnosticData", 1, RegistryValueKind.DWord)
        ]
    };

    public static Tweak ErrorReporting => new()
    {
        Id = "debloat.error_reporting",
        Name = "Turn off Windows Error Reporting",
        Description =
            "Stops Windows collecting crash reports and uploading them to Microsoft after an app or game " +
            "crashes, which otherwise means a burst of disk and network work right after the crash.",
        Category = TweakCategory.Privacy,
        Risk = RiskLevel.Safe,
        IncludeInOneClick = false,
        Warning =
            "Microsoft and game developers stop receiving crash reports from this device, and the " +
            "\"check for solutions\" prompt after a crash no longer appears.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                "Disabled", 1, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting",
                "Disabled", 1, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Blocked by policy rather than uninstalled: uninstalling cannot be reverted without downloading
    /// OneDrive again, while the policy comes off cleanly and OneDrive starts at the next sign-in.
    /// Revert does not start it from here, because this app runs elevated and OneDrive would then
    /// run as administrator.
    /// </summary>
    public static Tweak OneDrive => new()
    {
        Id = "debloat.onedrive",
        Name = "Turn off OneDrive",
        Description =
            "Stops OneDrive running and syncing, so it never starts an upload or download in the middle of a " +
            "game. Game cloud saves go through Steam, Xbox and the other launchers, not OneDrive.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "Files that are only in the cloud (the cloud icon in File Explorer) cannot be opened on this " +
            "device until you revert. If your Desktop or Documents are backed up to OneDrive, make sure " +
            "they are set to \"Always keep on this device\" first. After reverting, OneDrive starts again " +
            "at your next sign-in.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\OneDrive",
                "DisableFileSyncNGSC", 1, RegistryValueKind.DWord)
        ],
        ScriptApply = async (ctx, _, ct) =>
        {
            var outcome = await ctx.Runner.RunScriptAsync(
                """
                $p = Get-Process -Name OneDrive -ErrorAction SilentlyContinue
                if ($p) {
                    Write-Output 'Closing OneDrive'
                    $p | Stop-Process -Force -ErrorAction SilentlyContinue
                    Write-Output 'STOPPED'
                }
                """,
                "Close OneDrive",
                ct);

            return outcome.OutputLines.Contains("STOPPED")
                ? TweakResult.Ok("debloat.onedrive", "OneDrive closed.")
                : TweakResult.NoChange("debloat.onedrive");
        }
    };

    public static Tweak BackgroundApps => new()
    {
        Id = "debloat.background_apps",
        Name = "Disable Store app background activity",
        Description =
            "Stops packaged (Store) apps from running tasks in the background. Frees memory and stops " +
            "background CPU wake-ups while gaming. Desktop programs, Armoury Crate and all services are " +
            "unaffected.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Moderate,
        Warning =
            "Packaged apps will not deliver live tiles or push notifications until reverted. If you rely on " +
            "notifications from a Store app, leave this off.",
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                "GlobalUserDisabled", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Search",
                "BackgroundAppGlobalToggle", 0, RegistryValueKind.DWord)
        ]
    };

    private static string BuildTaskStateScript()
    {
        var list = string.Join(",\n    ", TelemetryTasks.Select(t => $"'{t}'"));

        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $tasks = @(
                {{list}}
            )
            foreach ($full in $tasks) {
                $leaf = Split-Path $full -Leaf
                $parent = (Split-Path $full -Parent) + '\'
                $t = Get-ScheduledTask -TaskPath $parent -TaskName $leaf -ErrorAction SilentlyContinue
                if ($t) { Write-Output "TASK=$full=$($t.State)" }
            }
            """;
    }

    private static string BuildTaskChangeScript(IReadOnlyCollection<string> tasks, bool disable)
    {
        var list = string.Join(",\n    ", tasks.Select(t => $"'{t}'"));
        var verb = disable ? "Disable-ScheduledTask" : "Enable-ScheduledTask";
        var word = disable ? "Disabling" : "Enabling";

        return $$"""
            $ErrorActionPreference = 'Continue'
            $failed = $false
            $tasks = @(
                {{list}}
            )
            foreach ($full in $tasks) {
                $leaf = Split-Path $full -Leaf
                $parent = (Split-Path $full -Parent) + '\'
                $t = Get-ScheduledTask -TaskPath $parent -TaskName $leaf -ErrorAction SilentlyContinue
                if ($null -eq $t) {
                    Write-Output "Not present on this build, skipping: $full"
                    continue
                }
                Write-Output "{{word}} scheduled task: $full"
                try { {{verb}} -TaskPath $parent -TaskName $leaf -ErrorAction Stop | Out-Null }
                catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
            }
            if ($failed) { exit 1 }
            """;
    }
}
