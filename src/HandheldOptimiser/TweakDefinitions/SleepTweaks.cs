using Microsoft.Win32;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Handhelds only have Modern Standby (S0), which keeps part of the system running and typically costs a
/// few percent of battery an hour. These keep a short standby for a quick pick-up, then hibernate, and
/// stop things waking the device in a bag.
///
/// Power settings are stored per power plan, and vendor tools switch plans with the performance mode
/// (Armoury Crate has one each for Silent, Performance and Turbo), so each setting is written to every
/// plan and each plan's previous value is journalled.
/// </summary>
public static class SleepTweaks
{
    private const string SleepSubgroup = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string NoSubgroup = "fea3413e-7e05-4911-9a71-700331f1c294";

    private const string HibernateAfterSetting = "9d7815a6-7ee4-497e-8888-515a05f02364";
    private const string ConnectivityInStandbySetting = "f15576e8-98b7-4186-b944-eafa664402d9";

    private const int HibernateAfterSeconds = 15 * 60;

    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public static IReadOnlyList<Tweak> All =>
    [
        HibernateAfterStandby,
        DisconnectInStandby,
        NoWakeDevices,
        DisableCardReader,
        FastStartup
    ];

    public static Tweak HibernateAfterStandby => PowerSetting(
        id: "sleep.hibernateafter",
        name: "Hibernate after 15 minutes of sleep",
        description:
            "On battery, hibernates after 15 minutes of sleep so it uses no power at all. Sleep alone " +
            "drains a few percent of battery an hour.",
        warning:
            "After 15 minutes asleep, waking takes 10 to 20 seconds instead of being instant. Your game " +
            "and apps stay where you left them.",
        subgroup: SleepSubgroup,
        setting: HibernateAfterSetting,
        ac: null,
        dc: HibernateAfterSeconds,
        needsHibernate: true);

    public static Tweak DisconnectInStandby => PowerSetting(
        id: "sleep.disconnected",
        name: "Turn Wi-Fi off during sleep",
        description:
            "Disconnects the network during sleep to save battery. It reconnects as soon as you wake it.",
        warning:
            "Downloads and updates pause while the handheld is asleep.",
        subgroup: NoSubgroup,
        setting: ConnectivityInStandbySetting,
        ac: 0,
        dc: 0,
        needsHibernate: false);

    /// <summary>
    /// One power setting, written to every power plan. A null value leaves that power source alone.
    /// </summary>
    private static Tweak PowerSetting(
        string id,
        string name,
        string description,
        string warning,
        string subgroup,
        string setting,
        int? ac,
        int? dc,
        bool needsHibernate) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Category = TweakCategory.Sleep,
        Risk = RiskLevel.Moderate,
        Warning = warning,
        IncludeInOneClick = false,

        ScriptDetect = async (ctx, ct) =>
        {
            var plans = await ReadPlansAsync(ctx, subgroup, setting, ct);
            if (plans.Count == 0)
            {
                return TweakState.Unknown;
            }

            var matching = plans.Count(p => Matches(p, ac, dc));
            return matching == plans.Count ? TweakState.Applied
                : matching == 0 ? TweakState.NotApplied
                : TweakState.Partial;
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            if (needsHibernate && !await EnsureHibernateAsync(ctx, journal, id, ct))
            {
                return TweakResult.Fail(id, "Hibernate could not be switched on, so this would leave the device asleep. See log.");
            }

            var plans = await ReadPlansAsync(ctx, subgroup, setting, ct);
            if (plans.Count == 0)
            {
                return TweakResult.Fail(id, "Could not read the power plans. See log.");
            }

            var commands = new List<string>();

            foreach (var plan in plans.Where(p => !Matches(p, ac, dc)))
            {
                // Only what is about to change is journalled, so revert puts back what was found.
                if (ac is { } acValue && plan.Ac != acValue)
                {
                    journal.CapturedState[$"plan.{plan.Scheme}.ac"] = plan.Ac.ToString();
                    commands.Add($"Set-Value '/setacvalueindex' '{plan.Scheme}' {acValue}");
                }

                if (dc is { } dcValue && plan.Dc != dcValue)
                {
                    journal.CapturedState[$"plan.{plan.Scheme}.dc"] = plan.Dc.ToString();
                    commands.Add($"Set-Value '/setdcvalueindex' '{plan.Scheme}' {dcValue}");
                }
            }

            if (commands.Count == 0)
            {
                return TweakResult.NoChange(id);
            }

            return await WriteAsync(ctx, subgroup, setting, commands, $"Apply \"{name}\" to every power plan", ct)
                ? TweakResult.Ok(id, $"\"{name}\" applied.")
                : TweakResult.Fail(id, "One or more power plans could not be changed. See log.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            var commands = new List<string>();

            foreach (var (key, value) in journal.CapturedState)
            {
                // plan.{scheme}.{ac|dc}; both parts are checked because they go into an elevated script.
                var parts = key.Split('.');
                if (parts.Length != 3 || parts[0] != "plan" || !Guid.TryParse(parts[1], out var scheme)
                    || !uint.TryParse(value, out var original))
                {
                    continue;
                }

                var verb = parts[2] switch { "ac" => "/setacvalueindex", "dc" => "/setdcvalueindex", _ => null };
                if (verb is not null)
                {
                    commands.Add($"Set-Value '{verb}' '{scheme:D}' {original}");
                }
            }

            var ok = commands.Count == 0
                || await WriteAsync(ctx, subgroup, setting, commands, $"Restore power plans for \"{name}\"", ct);

            if (journal.CapturedState.ContainsKey(HibernateSwitchedOn))
            {
                var off = await ctx.Runner.RunScriptAsync("& powercfg.exe /hibernate off", "Switch hibernate back off", ct);
                ok &= off.Succeeded;
            }

            return ok
                ? TweakResult.Ok(id, $"\"{name}\" reverted.")
                : TweakResult.Fail(id, "One or more power plans could not be restored. See log.");
        }
    };

    private sealed record PlanValue(string Scheme, uint Ac, uint Dc);

    private static bool Matches(PlanValue plan, int? ac, int? dc) =>
        (ac is null || plan.Ac == ac) && (dc is null || plan.Dc == dc);

    /// <summary>
    /// The setting's current value in every power plan. The plan list and the values are picked out of
    /// powercfg's output by shape (GUIDs, and the trailing hex values) rather than by its labels, which
    /// are translated on non-English Windows. A plan that does not report the setting cleanly is left out.
    /// </summary>
    private static async Task<List<PlanValue>> ReadPlansAsync(TweakContext ctx, string subgroup, string setting, CancellationToken ct)
    {
        var outcome = await ctx.Runner.RunScriptAsync(
            $$"""
            $plans = & powercfg.exe /list | Select-String '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}' | ForEach-Object { $_.Matches[0].Value }
            foreach ($plan in $plans) {
                $hex = @(& powercfg.exe /q $plan {{subgroup}} {{setting}} 2>$null | Select-String '(0x[0-9a-fA-F]{8})\s*$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
                # A list setting prints 2 values (AC, DC); a range setting prints min, max and step first.
                if ($hex.Count -eq 2 -or $hex.Count -eq 5) {
                    Write-Output ("PLAN={0}={1}={2}" -f $plan, [Convert]::ToUInt32($hex[-2], 16), [Convert]::ToUInt32($hex[-1], 16))
                }
            }
            """,
            "Read a power setting from every power plan",
            ct,
            echoScript: false);

        var plans = new List<PlanValue>();

        foreach (var line in outcome.OutputLines.Where(l => l.StartsWith("PLAN=", StringComparison.Ordinal)))
        {
            var parts = line.Split('=');
            if (parts.Length == 4 && Guid.TryParse(parts[1], out var scheme)
                && uint.TryParse(parts[2], out var acValue) && uint.TryParse(parts[3], out var dcValue))
            {
                plans.Add(new PlanValue(scheme.ToString("D"), acValue, dcValue));
            }
        }

        return plans;
    }

    private static async Task<bool> WriteAsync(
        TweakContext ctx, string subgroup, string setting, List<string> commands, string description, CancellationToken ct)
    {
        var outcome = await ctx.Runner.RunScriptAsync(
            $$"""
            $ErrorActionPreference = 'Continue'
            $script:failed = $false
            function Set-Value($verb, $plan, $value) {
                & powercfg.exe $verb $plan {{subgroup}} {{setting}} $value
                if ($LASTEXITCODE -ne 0) { Write-Output "powercfg $verb failed for plan $plan (exit $LASTEXITCODE)"; $script:failed = $true }
            }
            {{string.Join("\n", commands)}}
            # Settings only take effect when a plan is activated, so re-activate the one in use.
            $active = & powercfg.exe /getactivescheme | Select-String '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}' | ForEach-Object { $_.Matches[0].Value }
            if ($active) { & powercfg.exe /setactive $active }
            if ($script:failed) { exit 1 }
            """,
            description,
            ct);

        return outcome.Succeeded;
    }

    /// <summary>
    /// Fast Startup hibernates the kernel and drivers at shut down instead of closing them. Turning it off
    /// does not touch hibernate itself, so "Hibernate after 15 minutes of sleep" keeps working.
    /// </summary>
    public static Tweak FastStartup => new()
    {
        Id = "sleep.faststartup",
        Name = "Turn off Fast Startup",
        Description =
            "Makes Shut down a full shut down. Fast Startup is a common cause of controllers, Wi-Fi or " +
            "power limits misbehaving until you restart.",
        Category = TweakCategory.Sleep,
        Risk = RiskLevel.Safe,
        IncludeInOneClick = false,
        Warning = "Starting up from a shut down takes a few seconds longer.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                "HiberbootEnabled", 0, RegistryValueKind.DWord)
        ]
    };

    private const string HibernateSwitchedOn = "hibernate.switchedon";

    /// <summary>
    /// Hibernating after standby needs hibernate itself to be on. If it was switched off, it is switched
    /// on and that is journalled, so revert turns it back off.
    /// </summary>
    private static async Task<bool> EnsureHibernateAsync(TweakContext ctx, TweakJournalEntry journal, string id, CancellationToken ct)
    {
        // Absent means the default, which is on for any device that supports it.
        if (ctx.Registry.ReadValue(RegistryRoot.LocalMachine, PowerKey, "HibernateEnabled") is not 0)
        {
            return true;
        }

        var outcome = await ctx.Runner.RunScriptAsync("& powercfg.exe /hibernate on", "Switch hibernate on", ct);
        if (!outcome.Succeeded)
        {
            return false;
        }

        journal.CapturedState[HibernateSwitchedOn] = "1";
        return true;
    }

    private const string WakeDevices = "wake.devices";
    private const char DeviceSeparator = '\n';

    /// <summary>
    /// powercfg prints one device per line, and a placeholder line when there are none ("NONE" in English,
    /// translated elsewhere). Keeping only names that are also in the list of devices that can be
    /// configured to wake drops the placeholder in any language.
    /// </summary>
    private const string ListArmedScript =
        """
        $programmable = @(& powercfg.exe /devicequery wake_programmable | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        & powercfg.exe /devicequery wake_armed | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $programmable -contains $_ } | ForEach-Object { Write-Output ("DEV=" + $_) }
        """;

    public static Tweak NoWakeDevices => new()
    {
        Id = "sleep.wakedevices",
        Name = "Only the power button wakes the handheld",
        Description =
            "Stops keyboards, mice, controllers and dongles waking the handheld, so a bump in a bag does " +
            "not run the battery down.",
        Category = TweakCategory.Sleep,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "Use the power button to wake the handheld. Devices you plug in later are not covered until " +
            "you apply this again.",

        ScriptDetect = async (ctx, ct) =>
        {
            var outcome = await ctx.Runner.RunScriptAsync(ListArmedScript, "List devices allowed to wake the handheld", ct, echoScript: false);
            if (!outcome.Succeeded)
            {
                return TweakState.Unknown;
            }

            return ArmedDevices(outcome).Count == 0 ? TweakState.Applied : TweakState.NotApplied;
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            var listed = await ctx.Runner.RunScriptAsync(ListArmedScript, "List devices allowed to wake the handheld", ct, echoScript: false);
            var devices = ArmedDevices(listed);

            if (devices.Count == 0)
            {
                return TweakResult.NoChange("sleep.wakedevices");
            }

            journal.CapturedState[WakeDevices] = string.Join(DeviceSeparator, devices);

            var outcome = await ctx.Runner.RunScriptAsync(
                WakeScript("/devicedisablewake", devices),
                $"Stop {devices.Count} device(s) waking the handheld",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("sleep.wakedevices", "Only the power button wakes the handheld now.")
                : TweakResult.Fail("sleep.wakedevices", "One or more devices could not be changed. See log.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue(WakeDevices, out var saved) || string.IsNullOrWhiteSpace(saved))
            {
                return TweakResult.NoChange("sleep.wakedevices");
            }

            var devices = saved.Split(DeviceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // A device that has since been unplugged cannot be re-armed; that is not a failed revert.
            await ctx.Runner.RunScriptAsync(
                WakeScript("/deviceenablewake", devices),
                $"Let {devices.Count} device(s) wake the handheld again",
                ct);

            return TweakResult.Ok("sleep.wakedevices", "Devices can wake the handheld again.");
        }
    };

    private const string CardReaders = "sdreader.devices";

    /// <summary>
    /// SD host controllers on the PCI bus, found by the standard class code (base 08, sub 05) in the
    /// hardware IDs rather than by vendor name. Prints each one's instance ID and whether it is disabled.
    /// </summary>
    private const string ListCardReadersScript =
        """
        Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
            Where-Object { $_.InstanceId -like 'PCI\*' -and ($_.HardwareID -match '&CC_0805') } |
            ForEach-Object { Write-Output ("SD={0}={1}" -f $_.InstanceId, [int]($_.Problem -eq 'CM_PROB_DISABLED')) }
        """;

    public static Tweak DisableCardReader => new()
    {
        Id = "sleep.sdreader",
        Name = "Turn off the SD card reader",
        Description =
            "Switches the SD card reader off. On the ROG Ally it drains the battery during sleep, even " +
            "with no card in it.",
        Category = TweakCategory.Sleep,
        Risk = RiskLevel.Breaking,
        IncludeInOneClick = false,
        OnlyFor = HandheldDevice.RogAlly,
        Warning =
            "SD cards stop working until you switch this back off.",

        ScriptDetect = async (ctx, ct) =>
        {
            var readers = CardReaderStates(await ctx.Runner.RunScriptAsync(ListCardReadersScript, "Find the SD card reader", ct, echoScript: false));
            if (readers.Count == 0)
            {
                // No reader on this device, or none Windows can see.
                return TweakState.Unknown;
            }

            var disabled = readers.Count(r => r.Disabled);
            return disabled == readers.Count ? TweakState.Applied
                : disabled == 0 ? TweakState.NotApplied
                : TweakState.Partial;
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            var readers = CardReaderStates(await ctx.Runner.RunScriptAsync(ListCardReadersScript, "Find the SD card reader", ct, echoScript: false));
            if (readers.Count == 0)
            {
                return TweakResult.Fail("sleep.sdreader", "No SD card reader was found on this device.");
            }

            var toDisable = readers.Where(r => !r.Disabled).Select(r => r.InstanceId).ToList();
            if (toDisable.Count == 0)
            {
                return TweakResult.NoChange("sleep.sdreader");
            }

            journal.CapturedState[CardReaders] = string.Join(DeviceSeparator, toDisable);

            var outcome = await ctx.Runner.RunScriptAsync(
                CardReaderScript("Disable-PnpDevice", toDisable),
                "Turn off the SD card reader",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("sleep.sdreader", "The SD card reader is off.")
                : TweakResult.Fail("sleep.sdreader", "The SD card reader could not be turned off. See log.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue(CardReaders, out var saved))
            {
                return TweakResult.NoChange("sleep.sdreader");
            }

            // Only PCI instance IDs are accepted back from the journal: they go into an elevated script.
            var devices = saved
                .Split(DeviceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsPciInstanceId)
                .ToList();

            if (devices.Count == 0)
            {
                return TweakResult.NoChange("sleep.sdreader");
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                CardReaderScript("Enable-PnpDevice", devices),
                "Turn the SD card reader back on",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("sleep.sdreader", "The SD card reader is back on.")
                : TweakResult.Fail("sleep.sdreader", "The SD card reader could not be turned back on. See log.");
        }
    };

    private static List<(string InstanceId, bool Disabled)> CardReaderStates(ProcessOutcome outcome) =>
        outcome.OutputLines
            .Where(l => l.StartsWith("SD=", StringComparison.Ordinal))
            .Select(l => l.Split('='))
            .Where(p => p.Length == 3 && IsPciInstanceId(p[1]))
            .Select(p => (p[1], p[2].Trim() == "1"))
            .ToList();

    private static bool IsPciInstanceId(string id) =>
        id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '\\' or '&' or '_');

    private static string CardReaderScript(string cmdlet, IEnumerable<string> instanceIds) =>
        "$ErrorActionPreference = 'Stop'\n"
        + string.Join("\n", instanceIds.Select(id => $"{cmdlet} -InstanceId '{id}' -Confirm:$false"));

    private static List<string> ArmedDevices(ProcessOutcome outcome) =>
        outcome.OutputLines
            .Where(l => l.StartsWith("DEV=", StringComparison.Ordinal))
            .Select(l => l["DEV=".Length..].Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string WakeScript(string verb, IEnumerable<string> devices)
    {
        // Device names come from powercfg (or the journal) and go into an elevated script, so each is a
        // single-quoted literal with its quotes doubled.
        var lines = devices.Select(d =>
            $"& powercfg.exe {verb} '{d.Replace("'", "''")}'; if ($LASTEXITCODE -ne 0) {{ $failed = $true }}");

        return "$failed = $false\n" + string.Join("\n", lines) + "\nif ($failed) { exit 1 }";
    }
}
