using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// How a re-apply's captured state joins what the journal already holds, and what an import from the
/// old per-user journal file is not allowed to bring with it. Both are pure rules, tested without HKLM.
/// </summary>
public sealed class JournalCapturedStateTests
{
    [Fact]
    public void Device_lists_from_a_reapply_are_added_to_the_first_list()
    {
        var existing = new Dictionary<string, string> { [SleepTweaks.WakeDevices] = "Keyboard\nMouse" };
        var incoming = new Dictionary<string, string> { [SleepTweaks.WakeDevices] = "mouse\nXbox Controller" };

        var added = TweakJournalService.MergeCapturedState(existing, incoming);

        Assert.Equal(1, added);
        Assert.Equal(["Keyboard", "Mouse", "Xbox Controller"], existing[SleepTweaks.WakeDevices].Split('\n'));
    }

    [Fact]
    public void Card_reader_lists_are_merged_too()
    {
        var existing = new Dictionary<string, string> { [SleepTweaks.CardReaders] = @"PCI\VEN_1217&DEV_8621\4&1" };
        var incoming = new Dictionary<string, string> { [SleepTweaks.CardReaders] = @"PCI\VEN_1217&DEV_8621\4&2" };

        Assert.Equal(1, TweakJournalService.MergeCapturedState(existing, incoming));
        Assert.Equal([@"PCI\VEN_1217&DEV_8621\4&1", @"PCI\VEN_1217&DEV_8621\4&2"], existing[SleepTweaks.CardReaders].Split('\n'));
    }

    [Fact]
    public void A_list_with_nothing_new_is_not_counted_as_a_change()
    {
        var existing = new Dictionary<string, string> { [SleepTweaks.WakeDevices] = "Keyboard\nMouse" };
        var incoming = new Dictionary<string, string> { [SleepTweaks.WakeDevices] = "Mouse" };

        Assert.Equal(0, TweakJournalService.MergeCapturedState(existing, incoming));
        Assert.Equal("Keyboard\nMouse", existing[SleepTweaks.WakeDevices]);
    }

    [Fact]
    public void Single_values_keep_the_first_capture()
    {
        var existing = new Dictionary<string, string> { ["service.SysMain.start"] = "2" };
        var incoming = new Dictionary<string, string>
        {
            ["service.SysMain.start"] = "4",
            ["service.SysMain.delayed"] = "0"
        };

        var added = TweakJournalService.MergeCapturedState(existing, incoming);

        Assert.Equal(1, added);
        Assert.Equal("2", existing["service.SysMain.start"]);
        Assert.Equal("0", existing["service.SysMain.delayed"]);
    }

    [Fact]
    public void Imported_entries_lose_device_names_but_keep_checked_state()
    {
        var entry = new TweakJournalEntry
        {
            TweakId = "sleep.wakedevices",
            CapturedState =
            {
                [SleepTweaks.WakeDevices] = "Keyboard'; Remove-Item C:\\ -Recurse; '",
                [SleepTweaks.CardReaders] = @"PCI\VEN_1217&DEV_8621\4&1",
                ["plan.381b4222-f694-41f0-9685-ff5bb260df2e.dc"] = "0"
            }
        };

        var dropped = TweakJournalService.DropUntrustedLegacyState(entry);

        Assert.Equal([SleepTweaks.WakeDevices, SleepTweaks.CardReaders], dropped);
        Assert.False(entry.CapturedState.ContainsKey(SleepTweaks.WakeDevices));
        Assert.False(entry.CapturedState.ContainsKey(SleepTweaks.CardReaders));
        Assert.Equal("0", entry.CapturedState["plan.381b4222-f694-41f0-9685-ff5bb260df2e.dc"]);
    }

    [Fact]
    public void Entries_without_device_names_import_unchanged()
    {
        var entry = new TweakJournalEntry
        {
            TweakId = "debloat.telemetry",
            CapturedState = { [@"task.\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] = "Ready" }
        };

        Assert.Empty(TweakJournalService.DropUntrustedLegacyState(entry));
        Assert.Single(entry.CapturedState);
    }
}
