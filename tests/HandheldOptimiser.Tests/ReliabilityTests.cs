using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// A check that never answers is stopped, per-user settings go to the person signed in, and the Game
/// Bar widget's names for the pipe agree with the service's.
/// </summary>
public class ReliabilityTests
{
    // ----- Time limits -----

    [Fact]
    public async Task A_script_that_runs_past_its_time_limit_is_stopped_and_reported_as_timed_out()
    {
        var runner = new PowerShellRunner(TestLog.Instance);
        var clock = Stopwatch.StartNew();

        var outcome = await runner.RunScriptAsync(
            "Start-Sleep -Seconds 60", "Sleep for a minute", echoScript: false, timeout: TimeSpan.FromSeconds(1));

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Succeeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_cancel_from_the_caller_is_still_a_cancel_with_a_time_limit_set()
    {
        var runner = new PowerShellRunner(TestLog.Instance);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunScriptAsync(
            "Start-Sleep -Seconds 60", "Sleep for a minute", cancel.Token, echoScript: false, timeout: TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(90, "90 seconds")]
    [InlineData(120, "2 minutes")]
    [InlineData(600, "10 minutes")]
    public void A_time_limit_reads_as_seconds_or_minutes(int seconds, string expected) =>
        Assert.Equal(expected, PowerShellRunner.Describe(TimeSpan.FromSeconds(seconds)));

    // ----- Whose registry -----

    private const string Admin = "S-1-5-21-1-2-3-1001";
    private const string Player = "S-1-5-21-1-2-3-1002";

    [Fact]
    public void Running_as_the_signed_in_person_keeps_their_own_registry() =>
        Assert.Null(SignedInUser.ChooseOther(Player, Player, _ => true));

    [Fact]
    public void Running_as_another_account_uses_the_signed_in_persons_registry() =>
        Assert.Equal(Player, SignedInUser.ChooseOther(Admin, Player, sid => sid == Player));

    [Fact]
    public void A_registry_that_is_not_loaded_is_not_used() =>
        Assert.Null(SignedInUser.ChooseOther(Admin, Player, _ => false));

    [Theory]
    [InlineData(null, Player)]
    [InlineData(Admin, null)]
    [InlineData(Admin, "")]
    public void An_account_that_cannot_be_found_keeps_the_usual_registry(string? process, string? session) =>
        Assert.Null(SignedInUser.ChooseOther(process, session, _ => true));

    [Fact]
    public void SIDs_are_compared_without_regard_to_case() =>
        Assert.Null(SignedInUser.ChooseOther("s-1-5-21-1-2-3-1002", Player, _ => true));

    // ----- The widget's names -----

    [Fact]
    public void Every_field_the_widget_writes_or_reads_is_one_the_service_sends_or_takes()
    {
        var known = typeof(AutoTdpRequest).GetProperties().Select(p => p.Name)
            .Concat(typeof(AutoTdpStatus).GetProperties().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);

        var fields = typeof(AutoTdpProtocol.Fields).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.NotEmpty(fields);
        Assert.All(fields, name => Assert.Contains(name, known));
    }

    [Fact]
    public void The_widgets_fan_and_controller_buttons_name_choices_the_service_knows()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HandheldOptimiser.GameBar", "TdpWidget.xaml"));

        var fans = Tags(xaml, "OnFans");
        var modes = Tags(xaml, "OnControllerMode");

        // The widget's Default button sends no curve, and its tag is empty.
        Assert.Equal(AutoTdpProtocol.FanModes.Order(), fans.Where(t => t.Length > 0).Order());
        Assert.Equal(new[] { AutoTdpProtocol.DesktopMode, AutoTdpProtocol.GamepadMode }, modes.Order());
    }

    /// <summary>The Tag of every button whose Click goes to the named handler, in the order they appear.</summary>
    private static List<string> Tags(string xaml, string handler) =>
        Regex.Matches(xaml, $"<ToggleButton\\b[^>]*?Click=\"{handler}\"[^>]*>")
            .Select(m => Regex.Match(m.Value, "Tag=\"([^\"]*)\"").Groups[1].Value)
            .ToList();

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
