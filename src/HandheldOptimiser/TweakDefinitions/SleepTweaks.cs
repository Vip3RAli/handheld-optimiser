using Microsoft.Win32;
using HandheldOptimiser.Models;
using HandheldOptimiser.PowerService;
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
    private const string ButtonsSubgroup = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string NoSubgroup = "fea3413e-7e05-4911-9a71-700331f1c294";

    private const string HibernateAfterSetting = "9d7815a6-7ee4-497e-8888-515a05f02364";
    private const string PowerButtonSetting = "7648efa3-dd9c-4e3e-b566-50f929386280";
    private const string ConnectivityInStandbySetting = "f15576e8-98b7-4186-b944-eafa664402d9";

    private const int HibernateAfterSeconds = 15 * 60;

    /// <summary>What the power button does: 0 nothing, 1 sleep, 2 hibernate, 3 shut down, 4 display off.</summary>
    private const int PowerButtonHibernate = 2;

    /// <summary>
    /// The settings that only work with hibernate on. Reverting one of them leaves hibernate on while
    /// another is still set, or the device would be told to hibernate and have no way to.
    /// </summary>
    private static readonly (string Id, string Subgroup, string Setting, int? Ac, int? Dc)[] NeedHibernate =
    [
        ("sleep.hibernateafter", SleepSubgroup, HibernateAfterSetting, null, HibernateAfterSeconds),
        ("sleep.powerbutton", ButtonsSubgroup, PowerButtonSetting, null, PowerButtonHibernate)
    ];

    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public static IReadOnlyList<Tweak> All =>
    [
        HibernateAfterStandby,
        PowerButtonHibernates,
        DisconnectInStandby,
        NoWakeDevices,
        DisableCardReader,
        BatteryCare,
        FastStartup
    ];

    public static Tweak HibernateAfterStandby => PowerSetting(
        id: "sleep.hibernateafter",
        name: "Hibernate after 15 minutes of sleep",
        description:
            "On battery, hibernates after 15 minutes of sleep so it uses almost no power. Sleep alone " +
            "drains a few percent of battery an hour.",
        warning:
            "After 15 minutes asleep, waking takes 10 to 20 seconds instead of being instant. Your game " +
            "and apps stay where you left them.",
        subgroup: SleepSubgroup,
        setting: HibernateAfterSetting,
        ac: null,
        dc: HibernateAfterSeconds,
        needsHibernate: true);

    /// <summary>
    /// The timer above still leaves 15 minutes of sleep after every press. This removes them for a
    /// press of the button, and the timer goes on covering sleeps that start by themselves.
    /// </summary>
    public static Tweak PowerButtonHibernates => PowerSetting(
        id: "sleep.powerbutton",
        name: "Power button hibernates on battery",
        description:
            "On battery, pressing the power button hibernates the handheld, so it uses no power in a " +
            "bag. Plugged in, it still sleeps.",
        warning:
            "Waking takes 10 to 20 seconds instead of being instant. Most games carry on where you left " +
            "them, but some have to be restarted and online games lose their connection.",
        subgroup: ButtonsSubgroup,
        setting: PowerButtonSetting,
        ac: null,
        dc: PowerButtonHibernate,
        needsHibernate: true);

    public static Tweak DisconnectInStandby => PowerSetting(
        id: "sleep.disconnected",
        name: "Disconnect from the network during sleep",
        description:
            "Disconnects the network during sleep to save battery. It reconnects when the handheld wakes.",
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
        bool needsHibernate)
    {
        if (!needsHibernate)
        {
            return PowerPlanHelper.CreateTweak(
                id: id,
                name: name,
                description: description,
                category: TweakCategory.Sleep,
                risk: RiskLevel.Moderate,
                warning: warning,
                includeInOneClick: false,
                subgroup: subgroup,
                setting: setting,
                ac: ac,
                dc: dc);
        }

        return new()
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
                var plans = await PowerPlanHelper.ReadPlansAsync(ctx, subgroup, setting, ct);
                if (plans.Count == 0)
                {
                    return TweakState.Unknown;
                }

                var matching = plans.Count(p => PowerPlanHelper.Matches(p, ac, dc));
                return matching == plans.Count ? TweakState.Applied
                    : matching == 0 ? TweakState.NotApplied
                    : TweakState.Partial;
            },

            ScriptApply = async (ctx, journal, ct) =>
            {
                if (!await EnsureHibernateAsync(ctx, journal, id, ct))
                {
                    return TweakResult.Fail(id, "Hibernate could not be switched on, so this would leave the device asleep. The console has the detail.");
                }

                var plans = await PowerPlanHelper.ReadPlansAsync(ctx, subgroup, setting, ct);
                if (plans.Count == 0)
                {
                    return TweakResult.Fail(id, "Could not read the power plans. The console has the detail.");
                }

                var commands = PowerPlanHelper.BuildApplyCommands(plans, journal, ac, dc);
                if (commands.Count == 0)
                {
                    return TweakResult.NoChange(id);
                }

                return await PowerPlanHelper.WriteAsync(ctx, subgroup, setting, commands, $"Apply \"{name}\" to every power plan", ct)
                    ? TweakResult.Ok(id, $"\"{name}\" applied.")
                    : TweakResult.Fail(id, "One or more power plans could not be changed. The console has the detail.");
            },

            ScriptRevert = async (ctx, journal, ct) =>
            {
                var commands = PowerPlanHelper.BuildRevertCommands(journal);
                var ok = commands.Count == 0
                    || await PowerPlanHelper.WriteAsync(ctx, subgroup, setting, commands, $"Restore power plans for \"{name}\"", ct);

                // Hibernate goes back off if it was off before this app switched it on, whichever of the
                // settings that need it switched it on and whichever of them is reverted last.
                if (journal.CapturedState.ContainsKey(HibernateSwitchedOn) || HibernateWasOffBeforeUs(ctx))
                {
                    if (await AnotherSettingNeedsHibernateAsync(ctx, id, ct))
                    {
                        // Written down here as well, so undo data from before the shared record existed
                        // still lets the other setting's revert turn hibernate off.
                        RecordHibernateWasOff(ctx);
                        ctx.Log.Info("Hibernate is left on: another Battery & sleep setting still uses it.");
                    }
                    else
                    {
                        var off = await ctx.Runner.RunProcessAsync("powercfg.exe", ["/hibernate", "off"], "Switch hibernate back off", ct);
                        ok &= off.Succeeded;

                        if (off.Succeeded)
                        {
                            ClearHibernateWasOff(ctx);
                        }
                    }
                }

                return ok
                    ? TweakResult.Ok(id, $"\"{name}\" undone.")
                    : TweakResult.Fail(id, "One or more power plans could not be restored. The console has the detail.");
            }
        };
    }

    /// <summary>Whether any power plan still holds another setting that hibernates the device.</summary>
    private static async Task<bool> AnotherSettingNeedsHibernateAsync(TweakContext ctx, string revertingId, CancellationToken ct)
    {
        foreach (var other in NeedHibernate.Where(n => n.Id != revertingId))
        {
            var plans = await PowerPlanHelper.ReadPlansAsync(ctx, other.Subgroup, other.Setting, ct);
            if (plans.Any(p => PowerPlanHelper.Matches(p, other.Ac, other.Dc)))
            {
                return true;
            }
        }

        return false;
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
            "Shut down fully powers off, instead of saving a hibernation file to start faster. Fast " +
            "Startup is a common cause of controllers, Wi-Fi or power limits misbehaving until you restart.",
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

    /// <summary>
    /// Caps the ROG Ally's battery charge at 80% while plugged in, through ASUS's ACPI device 0x00120057
    /// (the same call Armoury Crate's Battery Care Mode uses), and keeps the choice in HKLM so the TDP
    /// Engine service puts it back after a restart or sleep.
    /// </summary>
    public static Tweak BatteryCare => new()
    {
        Id = "sleep.batterycare",
        Name = "Limit battery charge to 80%",
        Description =
            "Stops charging at 80% while plugged in, which reduces battery wear when playing on mains " +
            "power or a dock. TDP Engine re-applies the limit automatically after a restart or sleep.",
        Category = TweakCategory.Sleep,
        Risk = RiskLevel.Safe,
        IncludeInOneClick = false,
        OnlyFor = HandheldDevice.RogAlly,
        Warning = "Full battery runtime is 20% shorter until you turn this off before a trip.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, PowerService.BatteryCare.SettingsKey,
                PowerService.BatteryCare.LimitValue, PowerService.BatteryCare.CareLimit, RegistryValueKind.DWord)
        ],
        ScriptApply = async (ctx, _, ct) =>
        {
            if (ctx.Registry is RegistryHelper)
            {
                await NotifyBatteryCareAsync(ctx, on: true, ct);
            }

            return TweakResult.NoChange("sleep.batterycare");
        },
        ScriptRevert = async (ctx, _, ct) =>
        {
            if (ctx.Registry is RegistryHelper)
            {
                await NotifyBatteryCareAsync(ctx, on: false, ct);
            }

            return TweakResult.Ok("sleep.batterycare", "Battery charge limit restored to 100%.");
        }
    };

    private static async Task NotifyBatteryCareAsync(TweakContext ctx, bool on, CancellationToken ct)
    {
        var client = new TdpEngineClient(ctx.Log);
        var status = await client.SendAsync(new AutoTdpRequest(AutoTdpProtocol.SetBatteryCare, BatteryCare: on), ct);
        if (status?.Error is { } problem)
        {
            ctx.Log.Warning(problem);
        }
    }

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

        // Run directly rather than through a script, whose exit code would not be powercfg's.
        var outcome = await ctx.Runner.RunProcessAsync("powercfg.exe", ["/hibernate", "on"], "Switch hibernate on", ct);
        if (!outcome.Succeeded)
        {
            return false;
        }

        journal.CapturedState[HibernateSwitchedOn] = "1";
        RecordHibernateWasOff(ctx);
        return true;
    }

    /// <summary>
    /// Where "hibernate was off before this app switched it on" is kept for every setting that needs
    /// hibernate, not only in the undo data of the one that happened to switch it on. Otherwise reverting
    /// that one first, while another still needed hibernate, left hibernate on for good. In HKLM, next to
    /// the app's other machine-wide records, so only an administrator can plant it.
    /// </summary>
    private const string MachineStateKey = @"SOFTWARE\HandheldOptimiser";
    private const string HibernateWasOffValue = "HibernateOffBeforeUs";

    private static bool HibernateWasOffBeforeUs(TweakContext ctx) =>
        ctx.Registry.ReadValue(RegistryRoot.LocalMachine, MachineStateKey, HibernateWasOffValue) is 1;

    private static void RecordHibernateWasOff(TweakContext ctx)
    {
        if (HibernateWasOffBeforeUs(ctx))
        {
            return;
        }

        if (ctx.Registry.WriteValue(new RegistryValueSpec(
                RegistryRoot.LocalMachine, MachineStateKey, HibernateWasOffValue, 1, RegistryValueKind.DWord)) is null)
        {
            ctx.Log.Warning("Could not record that hibernate was off before; reverting may leave it on.");
        }
    }

    private static void ClearHibernateWasOff(TweakContext ctx)
    {
        if (!HibernateWasOffBeforeUs(ctx))
        {
            return;
        }

        ctx.Registry.RestoreSnapshot(new RegistryValueSnapshot
        {
            Root = RegistryRoot.LocalMachine,
            SubKey = MachineStateKey,
            ValueName = HibernateWasOffValue,
            KeyExisted = true,
            ValueExisted = false
        });
    }

    /// <summary>
    /// Undo data keys that hold a list of device names, one per line. Re-applying adds to the list
    /// rather than replacing it (see <see cref="TweakJournalService"/>), so every device either run
    /// changed is put back.
    /// </summary>
    public const string WakeDevices = "wake.devices";
    public const char DeviceSeparator = '\n';

    /// <summary>
    /// powercfg prints one device per line, and a placeholder line when there are none ("NONE" in English,
    /// translated elsewhere). Keeping only names that are also in the list of devices that can be
    /// configured to wake drops the placeholder in any language.
    /// </summary>
    internal const string ListArmedScript =
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
            var devices = UsableDeviceNames(ctx, ArmedDevices(listed));

            if (devices.Count == 0)
            {
                return TweakResult.NoChange("sleep.wakedevices");
            }

            journal.CapturedState[WakeDevices] = string.Join(DeviceSeparator, devices);

            var outcome = await ctx.Runner.RunScriptAsync(
                WakeScript("/devicedisablewake", devices),
                $"Stop {devices.Count} {(devices.Count == 1 ? "device" : "devices")} waking the handheld",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("sleep.wakedevices", "Only the power button wakes the handheld now.")
                : TweakResult.Fail("sleep.wakedevices", "One or more devices could not be changed. The console has the detail.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue(WakeDevices, out var saved) || string.IsNullOrWhiteSpace(saved))
            {
                return TweakResult.NoChange("sleep.wakedevices");
            }

            var devices = UsableDeviceNames(ctx,
                saved.Split(DeviceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (devices.Count == 0)
            {
                return TweakResult.NoChange("sleep.wakedevices");
            }

            // A device that has since been unplugged cannot be re-armed; that is not a failed revert.
            await ctx.Runner.RunScriptAsync(
                WakeScript("/deviceenablewake", devices),
                $"Let {devices.Count} {(devices.Count == 1 ? "device" : "devices")} wake the handheld again",
                ct);

            return TweakResult.Ok("sleep.wakedevices", "Devices can wake the handheld again.");
        }
    };

    public const string CardReaders = "sdreader.devices";

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
            "SD cards stop working while this is on. Turn it off to use a card again.",

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
                : TweakResult.Fail("sleep.sdreader", "The SD card reader could not be turned off. The console has the detail.");
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
                : TweakResult.Fail("sleep.sdreader", "The SD card reader could not be turned back on. The console has the detail.");
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

    /// <summary>
    /// Device names go into an elevated script. One with a line break or other control character in it
    /// did not come from a real device, so it is left out rather than passed on.
    /// </summary>
    private static List<string> UsableDeviceNames(TweakContext ctx, IEnumerable<string> names)
    {
        var usable = new List<string>();

        foreach (var name in names)
        {
            if (PsQuote.HasControlCharacters(name))
            {
                ctx.Log.Warning("Skipped a device name with a control character in it.");
                continue;
            }

            usable.Add(name);
        }

        return usable;
    }

    private static string WakeScript(string verb, IEnumerable<string> devices)
    {
        // Device names come from powercfg (or the journal) and go into an elevated script, so each is a
        // single-quoted literal with every form of quote PowerShell recognises doubled.
        var lines = devices.Select(d =>
            $"& powercfg.exe {verb} {PsQuote.Literal(d)}; if ($LASTEXITCODE -ne 0) {{ $failed = $true }}");

        return "$failed = $false\n" + string.Join("\n", lines) + "\nif ($failed) { exit 1 }";
    }
}
