using System.IO;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Copy diagnostics is pasted into a public issue, so the report must leave the player's name out, fit
/// in one GitHub comment whatever the logs hold, and keep the newest lines when it has to cut.
/// </summary>
public sealed class DiagnosticsReportTests
{
    private static DiagnosticsFacts Facts(params (string Heading, string Text)[] logs) => new()
    {
        AppVersion = "1.3.0",
        Windows = "Windows 11 Core 25H2 (build 26300.1)",
        Device = "ASUSTeK COMPUTER INC. ROG Ally RC71L, BIOS 342, recognised as RogAlly",
        ProfileFolder = @"C:\Users\Robin",
        UserName = "Robin",
        MachineName = "ROBINS-ALLY",
        Tweaks =
        [
            ("Turn off Widgets", TweakState.Applied),
            ("Turn off Fast Startup", TweakState.Applied),
            ("Turn off OneDrive", TweakState.NotApplied),
            ("Turn off Memory Integrity", null)
        ],
        UndoEntries = ["Turn off Widgets"],
        Logs = logs
    };

    [Fact]
    public void The_report_names_the_version_the_device_and_each_tweak_under_its_state()
    {
        var report = DiagnosticsReport.Build(Facts(("This session", "12:00:00 - started")));

        Assert.Contains("- Version: 1.3.0", report);
        Assert.Contains("- Device: ASUSTeK COMPUTER INC. ROG Ally RC71L", report);
        Assert.Contains("- On (2): Turn off Widgets; Turn off Fast Startup", report);
        Assert.Contains("- Off (1): Turn off OneDrive", report);
        Assert.Contains("- Not checked (1): Turn off Memory Integrity", report);
        Assert.Contains("- Undo data (1): Turn off Widgets", report);
        Assert.Contains("### This session\n\n```\n12:00:00 - started\n```", report);
    }

    [Fact]
    public void The_players_name_and_the_devices_name_are_taken_out_of_the_logs()
    {
        var report = DiagnosticsReport.Build(Facts(("Game library",
            @"Screenshot saved: C:\Users\Robin\Pictures\Screenshots\Hades.png" + "\n" +
            @"Signed in as ROBINS-ALLY\robin")));

        Assert.Contains(@"%USERPROFILE%\Pictures\Screenshots\Hades.png", report);
        Assert.Contains(@"Signed in as <device>\<user>", report);
        Assert.DoesNotContain("Robin", report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("al", "The Ally is also plugged in", "The Ally is also plugged in")]
    [InlineData("Sam", "Samsung panel, played by Sam.", "Samsung panel, played by <user>.")]
    [InlineData(null, "nothing to hide", "nothing to hide")]
    public void A_name_is_only_replaced_as_a_whole_word_and_never_when_it_is_very_short(string? name, string text, string expected) =>
        Assert.Equal(expected, DiagnosticsReport.Scrub(text, null, name, null));

    [Fact]
    public void Only_the_end_of_a_log_is_kept()
    {
        var log = string.Join("\r\n", Enumerable.Range(1, 500).Select(n => $"line {n}"));

        var tail = DiagnosticsReport.Tail(log, 150);

        Assert.StartsWith("line 351\n", tail);
        Assert.EndsWith("line 500", tail);
    }

    [Fact]
    public void A_report_with_very_long_logs_still_fits_in_one_comment_and_keeps_every_logs_newest_line()
    {
        var wide = string.Join("\n", Enumerable.Range(1, 150).Select(n => $"{n} " + new string('x', 1000)));

        var report = DiagnosticsReport.Build(Facts(
            ("This session", wide + "\nsession end"), ("Game library", wide + "\nlibrary end"), ("TDP Engine", wide + "\nengine end")));

        Assert.True(report.Length <= DiagnosticsReport.MaxLength, $"{report.Length} characters");
        Assert.Contains("session end\n```", report);
        Assert.Contains("library end\n```", report);
        Assert.Contains("engine end\n```", report);
        Assert.Contains("(earlier lines left out to fit)", report);
    }

    [Fact]
    public void Cutting_a_text_to_fit_drops_whole_lines_from_its_start()
    {
        var cut = DiagnosticsReport.Fit("first line\nsecond line\nthird line\nfourth line\nfifth line", 55);

        Assert.Equal("(earlier lines left out to fit)\nfourth line\nfifth line", cut);
    }

    [Fact]
    public void A_log_that_is_not_there_says_so()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-log-{Guid.NewGuid():N}.log");

        Assert.Equal("(no such log on this device)", DiagnosticsReport.ReadLogTail(missing));
    }
}
