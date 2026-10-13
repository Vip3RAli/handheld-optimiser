using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The read-only rows on Device &amp; System Health: sleep, power plans and storage. Each is built from
/// text or numbers Windows printed, so the tests feed them what a ROG Ally printed on 13 October 2026,
/// and what Windows prints when there is something to report.
/// </summary>
public sealed class DeviceReportTests
{
    private const long Hour = TimeSpan.TicksPerHour;

    private static string Session(string type, int ac, double hours, int entry, int exit, int full = 40000) =>
        $"{type}|{ac}|{(long)(hours * Hour)}|{entry}|{exit}|{full}";

    [Fact]
    public void A_sleep_session_is_read_from_its_six_fields()
    {
        var session = Assert.Single(SleepHealth.ParseSessions(["Screen Off|0|867283930|31428|31000|31428"]));

        Assert.Equal("Screen Off", session.Type);
        Assert.True(session.OnBattery);
        Assert.Equal(86.7, session.Hours * 3600, 1);
        Assert.Equal(1.36, session.DrainPercent!.Value, 2);
    }

    [Fact]
    public void A_session_with_no_battery_reading_has_no_drain()
    {
        // What the Ally printed for a hibernate: every capacity 0.
        var session = Assert.Single(SleepHealth.ParseSessions(["Hibernate|1|339522888449|0|0|0"]));

        Assert.False(session.OnBattery);
        Assert.Null(session.DrainPercent);
    }

    [Fact]
    public void Lines_that_are_not_sessions_are_skipped()
    {
        Assert.Empty(SleepHealth.ParseSessions(["", "Screen Off|0|1", "Screen Off|0|soon|1|1|1"]));
    }

    [Fact]
    public void Sleeps_on_battery_are_added_up_into_a_rate()
    {
        var sessions = SleepHealth.ParseSessions(
        [
            Session("Screen Off", ac: 0, hours: 8, entry: 40000, exit: 38000),
            Session("Sleep", ac: 0, hours: 2, entry: 30000, exit: 29600)
        ]);

        var check = SleepHealth.BatteryAsleepCheck(sessions);

        Assert.Equal(HealthStatus.Good, check.Status);
        Assert.Equal(
            "On battery in the last 7 days: asleep for 10 hours over 2 sleeps, using 6% of the battery (0.6% an hour).",
            check.Detail);
        Assert.Null(check.Advice);
    }

    [Fact]
    public void A_high_rate_asleep_is_flagged_and_points_at_the_hibernate_settings()
    {
        var sessions = SleepHealth.ParseSessions([Session("Screen Off", ac: 0, hours: 5, entry: 40000, exit: 32000)]);

        var check = SleepHealth.BatteryAsleepCheck(sessions);

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Contains("(4.0% an hour)", check.Detail);
        Assert.Contains("Sleep & Battery", check.Advice);
    }

    [Fact]
    public void Only_real_sleeps_on_battery_are_measured()
    {
        var sessions = SleepHealth.ParseSessions(
        [
            Session("Screen Off", ac: 1, hours: 8, entry: 40000, exit: 20000),   // plugged in
            Session("Active", ac: 0, hours: 3, entry: 40000, exit: 10000),       // being played
            Session("Hibernate", ac: 0, hours: 9, entry: 40000, exit: 39900),    // not sleep
            Session("Screen Off", ac: 0, hours: 0.02, entry: 40000, exit: 39000),// too short to read
            Session("Screen Off", ac: 0, hours: 4, entry: 20000, exit: 30000),   // charged meanwhile
            Session("Screen Off", ac: 0, hours: 4, entry: 0, exit: 0, full: 0)   // no reading
        ]);

        var check = SleepHealth.BatteryAsleepCheck(sessions);

        Assert.Equal(HealthStatus.Info, check.Status);
        Assert.StartsWith("No sleep of 10 minutes or more on battery", check.Detail);
    }

    [Fact]
    public void No_sleep_study_reads_as_unknown_not_as_no_drain()
    {
        Assert.Equal(HealthStatus.Unknown, SleepHealth.BatteryAsleepCheck(null).Status);
    }

    [Fact]
    public void Nothing_holding_the_device_awake_is_good()
    {
        // The Ally, idle.
        string[] lines =
        [
            "DISPLAY:", "None.", "", "SYSTEM:", "None.", "", "AWAYMODE:", "None.", "",
            "EXECUTION:", "None.", "", "PERFBOOST:", "None.", "", "ACTIVELOCKSCREEN:", "None."
        ];

        var requests = SleepHealth.ParseRequests(lines);

        Assert.Empty(requests);
        Assert.Equal(HealthStatus.Good, SleepHealth.RequestsCheck(requests).Status);
    }

    [Fact]
    public void A_request_is_read_with_its_kind_and_reason_in_any_language()
    {
        // "Keine." is what German Windows prints for "None."; only the tags and headings are fixed.
        string[] lines =
        [
            "DISPLAY:",
            @"[PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe",
            "Video Wake Lock",
            "",
            "SYSTEM:",
            @"[DRIVER] Realtek High Definition Audio (HDAUDIO\FUNC_01&VEN_10EC)",
            "An audio stream is currently in use.",
            "[SERVICE] Some service with no reason",
            "",
            "AWAYMODE:",
            "Keine."
        ];

        var requests = SleepHealth.ParseRequests(lines);

        Assert.Equal(3, requests.Count);
        Assert.Equal(new PowerRequest("DISPLAY", lines[1], "Video Wake Lock"), requests[0]);
        Assert.Equal("SYSTEM", requests[1].Kind);
        Assert.Equal("An audio stream is currently in use.", requests[1].Why);
        Assert.Equal(string.Empty, requests[2].Why);

        var check = SleepHealth.RequestsCheck(requests);

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.StartsWith("[PROCESS] chrome.exe: Video Wake Lock", check.Detail);
        Assert.Contains("Realtek High Definition Audio", check.Detail);
    }

    [Fact]
    public void No_wake_timers_is_good_whatever_the_sentence_says()
    {
        var timers = SleepHealth.ParseWakeTimers(["There are no active wake timers in the system."]);

        Assert.Empty(timers);
        Assert.Equal("None", SleepHealth.WakeTimersCheck(timers).Detail);
    }

    [Fact]
    public void A_wake_timer_is_shown_with_its_reason()
    {
        string[] lines =
        [
            @"Timer set by [SERVICE] \Device\HarddiskVolume3\Windows\System32\svchost.exe (SystemEventsBroker) expires at 3:00:00 AM on 14/10/2026.",
            @"  Reason: Windows will execute 'NT TASK\Microsoft\Windows\UpdateOrchestrator\Reboot' scheduled task that requested waking the computer.",
            ""
        ];

        var timer = Assert.Single(SleepHealth.ParseWakeTimers(lines));

        Assert.Contains("SystemEventsBroker", timer);
        Assert.Contains("UpdateOrchestrator", timer);
        Assert.Equal(HealthStatus.Warning, SleepHealth.WakeTimersCheck([timer]).Status);
    }

    [Fact]
    public void Devices_that_can_wake_it_are_named()
    {
        Assert.Equal("Only the power button", SleepHealth.WakeDevicesCheck([]).Detail);
        Assert.Equal(HealthStatus.Unknown, SleepHealth.WakeDevicesCheck(null).Status);

        var check = SleepHealth.WakeDevicesCheck(["HID Keyboard Device", "USB Root Hub"]);

        Assert.Equal(HealthStatus.Warning, check.Status);
        Assert.Equal("HID Keyboard Device, USB Root Hub", check.Detail);
    }

    [Fact]
    public void The_last_wake_shows_the_facts_and_leaves_out_the_counts()
    {
        Assert.Equal("Windows has no record of it", SleepHealth.LastWakeCheck(["Wake History Count - 0"]).Detail);

        var check = SleepHealth.LastWakeCheck(
        [
            "Wake History Count - 1",
            "Wake History [0]",
            "  Wake Source Count - 1",
            "  Wake Source [0]",
            "    Type: Device",
            "    Friendly Name: Power Button"
        ]);

        Assert.Equal("Type: Device\nFriendly Name: Power Button", check.Detail);
        Assert.Equal(HealthStatus.Info, check.Status);
    }

    [Fact]
    public void Modern_Standby_with_hibernate_off_is_flagged()
    {
        var ally = new SleepStates(ModernStandby: true, ClassicSleep: false, Hibernate: true, HibernateFile: true);

        var ready = SleepHealth.SleepStatesCheck(ally);
        Assert.Equal(HealthStatus.Info, ready.Status);
        Assert.Equal(
            "Modern Standby, which keeps part of the system running while asleep. Hibernate is ready.",
            ready.Detail);

        var exposed = SleepHealth.SleepStatesCheck(ally with { HibernateFile = false });
        Assert.Equal(HealthStatus.Warning, exposed.Status);
        Assert.Contains("Hibernate is switched off", exposed.Detail);

        Assert.Equal(HealthStatus.Unknown, SleepHealth.SleepStatesCheck(null).Status);
    }

    /// <summary>What the Ally's Turbo plan printed for two of its settings, with the list above it.</summary>
    private static readonly string[] PlanLines =
    [
        "ACTIVE=6fecc5ae-f350-48a5-b669-b472cb895ccf",
        "LIST=Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)",
        "LIST=Power Scheme GUID: 6fecc5ae-f350-48a5-b669-b472cb895ccf  (Turbo) *",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=Power Scheme GUID: 6fecc5ae-f350-48a5-b669-b472cb895ccf  (Turbo)",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=  Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=      Possible Setting Index: 000",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=      Possible Setting Friendly Name: Disabled",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Current AC Power Setting Index: 0x00000000",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Current DC Power Setting Index: 0x00000000",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Power Setting GUID: 75b0ae3f-bce0-45a7-8c89-c9611c25e100  (Maximum processor frequency)",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=      Minimum Possible Setting: 0x00000000",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=      Maximum Possible Setting: 0xffffffff",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=      Possible Settings increment: 0x00000001",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Current AC Power Setting Index: 0x00000000",
        "QH=6fecc5ae-f350-48a5-b669-b472cb895ccf=    Current DC Power Setting Index: 0x00000fa0",
        "QH=381b4222-f694-41f0-9685-ff5bb260df2e=    Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)",
        "QH=381b4222-f694-41f0-9685-ff5bb260df2e=    Current AC Power Setting Index: 0x00000002",
        "QH=381b4222-f694-41f0-9685-ff5bb260df2e=    Current DC Power Setting Index: 0x00000002"
    ];

    [Fact]
    public void Power_plans_are_listed_with_the_one_in_use_first()
    {
        var plans = ProcessorPowerReport.ParsePlans(PlanLines);

        Assert.Equal(
            [
                new PowerPlan("6fecc5ae-f350-48a5-b669-b472cb895ccf", "Turbo", Active: true),
                new PowerPlan("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", Active: false)
            ],
            plans);
    }

    [Fact]
    public void A_plans_settings_are_its_last_two_values_and_no_other_plans()
    {
        var turbo = ProcessorPowerReport.ParseSettings(PlanLines, "6fecc5ae-f350-48a5-b669-b472cb895ccf");

        Assert.Equal((0u, 0u), turbo[ProcessorPowerReport.BoostMode]);
        Assert.Equal((0u, 4000u), turbo[ProcessorPowerReport.MaxFrequency]);
        Assert.False(turbo.ContainsKey(ProcessorPowerReport.EnergyPreference));

        var balanced = ProcessorPowerReport.ParseSettings(PlanLines, "381b4222-f694-41f0-9685-ff5bb260df2e");

        Assert.Equal((2u, 2u), balanced[ProcessorPowerReport.BoostMode]);
    }

    [Fact]
    public void Each_power_plan_gets_a_row_saying_what_it_holds()
    {
        var checks = ProcessorPowerReport.Checks(PlanLines);

        Assert.Equal(2, checks.Count);
        Assert.Equal("[Power plan] Turbo (in use)", checks[0].Name);
        Assert.StartsWith("Plugged in: boost off, no speed cap.\nOn battery: boost off, capped at 4000 MHz.\n", checks[0].Detail);
        Assert.EndsWith("so this app leaves them alone.", checks[0].Detail);
        Assert.Equal(HealthStatus.Info, checks[0].Status);
        Assert.Null(checks[0].Advice);

        Assert.Equal("[Power plan] Balanced", checks[1].Name);
        Assert.Equal("Plugged in: boost aggressive.\nOn battery: boost aggressive.", checks[1].Detail);
        Assert.Null(checks[1].Advice);
    }

    [Fact]
    public void No_answer_about_power_plans_is_one_unknown_row()
    {
        var check = Assert.Single(ProcessorPowerReport.Checks([]));

        Assert.Equal(HealthStatus.Unknown, check.Status);
    }

    [Fact]
    public void Free_space_is_flagged_below_twenty_gigabytes()
    {
        const long gb = 1024L * 1024 * 1024;

        var roomy = StorageHealth.FreeSpaceCheck("C:", 342 * gb, 930 * gb);
        Assert.Equal(HealthStatus.Good, roomy.Status);
        Assert.Equal("342 GB free of 930 GB", roomy.Detail);
        Assert.Null(roomy.OpenUri);

        var tight = StorageHealth.FreeSpaceCheck("C:", 12 * gb, 930 * gb);
        Assert.Equal(HealthStatus.Warning, tight.Status);
        Assert.Equal("ms-settings:storagesense", tight.OpenUri);
        Assert.NotNull(tight.Advice);
    }

    [Fact]
    public void A_card_that_is_not_NTFS_says_what_that_rules_out()
    {
        var exfat = StorageHealth.CardCheck("D:", "exFAT");

        Assert.Equal("[Storage] Card in D:", exfat.Name);
        Assert.Contains("Xbox app", exfat.Detail);
        Assert.Contains("erases", exfat.Advice);
        Assert.Equal(HealthStatus.Info, exfat.Status);

        var ntfs = StorageHealth.CardCheck("E:", "NTFS");

        Assert.StartsWith("NTFS.", ntfs.Detail);
        Assert.Null(ntfs.Advice);
    }

    [Fact]
    public void Cores_are_described_by_how_many_stay_awake()
    {
        const string plan = "6fecc5ae-f350-48a5-b669-b472cb895ccf";
        string[] lines =
        [
            $"ACTIVE={plan}",
            $"LIST=Power Scheme GUID: {plan}  (Turbo) *",
            $"QH={plan}=    Power Setting GUID: {ProcessorPowerReport.EnergyPreference}  (Processor energy performance preference policy)",
            $"QH={plan}=    Current AC Power Setting Index: 0x00000000",
            $"QH={plan}=    Current DC Power Setting Index: 0x0000003c",
            $"QH={plan}=    Power Setting GUID: {ProcessorPowerReport.MinCores}  (Processor performance core parking min cores)",
            $"QH={plan}=    Current AC Power Setting Index: 0x00000064",
            $"QH={plan}=    Current DC Power Setting Index: 0x00000019",
            $"QH={plan}=    Power Setting GUID: {ProcessorPowerReport.MaxCores}  (Processor performance core parking max cores)",
            $"QH={plan}=    Current AC Power Setting Index: 0x00000064",
            $"QH={plan}=    Current DC Power Setting Index: 0x00000064"
        ];

        var check = Assert.Single(ProcessorPowerReport.Checks(lines));

        Assert.StartsWith(
            "Plugged in: power saving preference 0 of 100, every core kept awake.\n" +
            "On battery: power saving preference 60 of 100, 25% to 100% of cores kept awake.\n",
            check.Detail);
    }

    [Theory]
    [InlineData("1|1", "On.")]
    [InlineData("0|0", "Off.")]
    [InlineData("0|1", "Encrypted, but not protected yet.")]
    [InlineData("0|2", "Part way")]
    public void Encryption_is_reported_and_never_marked_as_something_to_fix(string status, string starts)
    {
        var check = StorageHealth.EncryptionCheck(status);

        Assert.StartsWith(starts, check.Detail);
        Assert.Equal(HealthStatus.Info, check.Status);
        Assert.Equal("ms-settings:deviceencryption", check.OpenUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2|0")]
    public void No_answer_about_encryption_is_unknown(string? status)
    {
        Assert.Equal(HealthStatus.Unknown, StorageHealth.EncryptionCheck(status).Status);
    }

    [Theory]
    [InlineData("ms-settings:deviceencryption", true)]
    [InlineData("ms-windows-store://downloadsandupdates", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData("https://example.com", false)]
    [InlineData("shell:startup", false)]
    public void Only_Settings_and_Store_pages_are_opened(string uri, bool allowed)
    {
        Assert.Equal(allowed, ShellLinks.IsAllowed(uri));
    }
}
