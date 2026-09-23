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

    public static IReadOnlyList<Tweak> All =>
    [
        Telemetry,
        WebSearch,
        CortanaAndCopilot,
        ConsumerFeatures,
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
