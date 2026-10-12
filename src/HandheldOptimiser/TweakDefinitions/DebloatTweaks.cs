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
            "Cuts telemetry to the minimum Windows allows and turns off the advertising ID. Windows " +
            "Update and Defender are unaffected.",
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
            "Start menu search returns local results only, so it is instant and never hangs on flaky " +
            "Wi-Fi.",
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
            "Turns off Cortana and Copilot and removes the Copilot taskbar button.",
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
            "Stops Windows installing promoted apps, showing suggestions in Start and re-adding " +
            "bloatware you remove. Apply before removing bloatware.",
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
            "Turns off recommendations, account nags and tips in Start, Settings, File Explorer and the " +
            "lock screen, plus the \"finish setting up your device\" screen.",
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

    /// <summary>The Widgets board: the Windows Web Experience Pack, by package name, family and Store ID.</summary>
    public const string WidgetsPackage = "MicrosoftWindows.Client.WebExperience";
    private const string WidgetsFamily = "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy";
    private const string WidgetsStoreId = "9MSSGKG348SP";

    /// <summary>
    /// Removes the Widgets app for this user, because Windows leaves no setting to write. A driver
    /// blocks other programs from writing the taskbar's TaskbarDa value, and on current builds also
    /// the AllowNewsAndInterests policy this tweak used to set: on a ROG Ally an administrator could
    /// write any other value in that key and was refused that one.
    ///
    /// Only this user's registration goes. Windows usually keeps the app's files, so undoing it is a
    /// matter of registering them again, and the Store is only needed where they have gone.
    /// </summary>
    public static Tweak Widgets => new()
    {
        Id = "debloat.widgets",
        Name = "Turn off Widgets",
        Description =
            "Removes the Widgets board and its taskbar button, freeing 100 to 200 MB of memory. " +
            "Reverting puts it back, from the Microsoft Store if Windows has not kept a copy.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        // Nothing is written here any more. The policy stays listed so that undo data from a version
        // that did write it, on a Windows that allowed it, can still be put back.
        ScriptRegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, RegistryValueKind.DWord)
        ],
        ScriptDetect = async (ctx, ct) =>
        {
            // Nothing is printed when the question itself fails, so a broken package service reads as
            // unknown, not as Widgets already gone.
            var outcome = await ctx.Runner.RunScriptAsync(
                $$"""
                try {
                    if (Get-AppxPackage -Name '{{WidgetsPackage}}' -ErrorAction Stop) { Write-Output 'WIDGETS=Installed' }
                    else { Write-Output 'WIDGETS=Absent' }
                } catch { }
                """,
                "Check whether the Widgets app is installed",
                ct,
                echoScript: false);

            return WidgetsState(outcome.OutputLines);
        },
        ScriptApply = async (ctx, journal, ct) =>
        {
            if (Services.SafetyGuard.IsAppxProtected(WidgetsPackage, out var reason))
            {
                ctx.Log.Error($"BLOCKED removal of {WidgetsPackage}: {reason}");
                return TweakResult.Blocked("debloat.widgets", reason!);
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                $$"""
                $ErrorActionPreference = 'Continue'
                $failed = $false
                foreach ($p in Get-AppxPackage -Name '{{WidgetsPackage}}' -ErrorAction SilentlyContinue) {
                    Write-Output "Removing $($p.PackageFullName)"
                    try {
                        Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
                        Write-Output 'RESULT=REMOVED'
                    }
                    catch { Write-Output "  failed: $($_.Exception.Message)"; $failed = $true }
                }
                if ($failed) { exit 1 }
                """,
                "Remove the Widgets app",
                ct);

            if (!outcome.Succeeded)
            {
                return TweakResult.Fail("debloat.widgets", "The Widgets app could not be removed. See log.");
            }

            if (!outcome.OutputLines.Contains("RESULT=REMOVED"))
            {
                return TweakResult.NoChange("debloat.widgets");
            }

            // An entry with nothing in it is not kept, and a tweak with no entry cannot be reverted.
            journal.CapturedState["widgets.removed"] = WidgetsPackage;
            return TweakResult.Ok("debloat.widgets", "Widgets removed.");
        },
        ScriptRevert = async (ctx, journal, ct) =>
        {
            var register = await ctx.Runner.RunScriptAsync(
                $$"""
                $ErrorActionPreference = 'Continue'
                if (Get-AppxPackage -Name '{{WidgetsPackage}}' -ErrorAction SilentlyContinue) {
                    Write-Output 'RESULT=PRESENT'
                    exit 0
                }
                try {
                    Add-AppxPackage -RegisterByFamilyName -MainPackage '{{WidgetsFamily}}' -ErrorAction Stop
                    Write-Output 'RESULT=REGISTERED'
                } catch {
                    Write-Output "  Windows has not kept a copy: $($_.Exception.Message)"
                    Write-Output 'RESULT=MISSING'
                }
                """,
                "Put the Widgets app back",
                ct);

            if (register.OutputLines.Contains("RESULT=PRESENT"))
            {
                ctx.Log.Info("The Widgets app is installed; nothing to put back.");
                return TweakResult.NoChange("debloat.widgets");
            }

            if (register.OutputLines.Contains("RESULT=REGISTERED"))
            {
                return TweakResult.Ok("debloat.widgets", "Widgets restored.");
            }

            var store = await ctx.Runner.RunProcessAsync(
                "winget.exe",
                [
                    "install", "--id", WidgetsStoreId, "--exact", "--source", "msstore",
                    "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
                ],
                "Install the Widgets app from the Microsoft Store",
                ct);

            return store.Succeeded
                ? TweakResult.Ok("debloat.widgets", "Widgets restored from the Microsoft Store.")
                : TweakResult.Fail(
                    "debloat.widgets",
                    "Widgets could not be put back. Install \"Windows Web Experience Pack\" from the Microsoft Store.");
        }
    };

    /// <summary>
    /// The Widgets tweak's state, from the line its check prints: applied when the app is not installed
    /// for this user, and unknown when Windows gave no answer.
    /// </summary>
    public static TweakState WidgetsState(IEnumerable<string> outputLines) =>
        outputLines.FirstOrDefault(l => l.StartsWith("WIDGETS=", StringComparison.Ordinal)) switch
        {
            "WIDGETS=Absent" => TweakState.Applied,
            "WIDGETS=Installed" => TweakState.NotApplied,
            _ => TweakState.Unknown
        };

    public static Tweak EdgeBackground => new()
    {
        Id = "debloat.edge_background",
        Name = "Stop Edge running in the background",
        Description =
            "Stops Edge preloading at sign-in and staying open after you close it, freeing the memory it " +
            "holds.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Safe,
        Warning =
            "Edge opens slightly slower. Its settings page will say it is managed by your organisation, " +
            "which is just this tweak.",
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
            "Stops Windows recording which apps and files you open, and using diagnostic data to " +
            "personalise tips and offers.",
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
            "Stops Windows collecting and uploading crash reports, which avoids a burst of disk and " +
            "network work after a crash.",
        Category = TweakCategory.Privacy,
        Risk = RiskLevel.Safe,
        IncludeInOneClick = false,
        Warning =
            "Microsoft and game developers get no crash reports from this device, and the \"check for " +
            "solutions\" prompt no longer appears.",
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
            "Stops OneDrive running and syncing, so it never interrupts a game. Game cloud saves use " +
            "Steam, Xbox and the other launchers, not OneDrive.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "Cloud-only files cannot be opened until you revert. If Desktop or Documents are backed up " +
            "to OneDrive, set them to \"Always keep on this device\" first. After reverting, OneDrive " +
            "starts at your next sign-in.",
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
            "Stops Store apps running in the background, freeing memory and CPU. Desktop programs, " +
            "Armoury Crate and services are unaffected.",
        Category = TweakCategory.Debloat,
        Risk = RiskLevel.Moderate,
        Warning =
            "Store apps stop sending notifications until reverted. Leave this off if you rely on them.",
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
