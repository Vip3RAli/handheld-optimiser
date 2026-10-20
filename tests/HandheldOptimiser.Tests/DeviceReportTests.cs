using HandheldOptimiser.Services;
using HandheldOptimiser.ViewModels;
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
        Assert.Contains("Battery & sleep", check.Advice);
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
        Assert.Equal("No wake timers", SleepHealth.WakeTimersCheck(timers).Detail);
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
        Assert.Equal("Modern Standby, hibernate ready", ready.Summary);
        Assert.Equal(
            "Asleep, part of the system keeps running and uses the battery. Hibernating uses none.",
            ready.Detail);

        var exposed = SleepHealth.SleepStatesCheck(ally with { HibernateFile = false });
        Assert.Equal(HealthStatus.Warning, exposed.Status);
        Assert.Equal("Modern Standby, hibernate off", exposed.Summary);
        Assert.Contains("no hibernate to fall back on", exposed.Detail);

        var classic = SleepHealth.SleepStatesCheck(new SleepStates(ModernStandby: false, ClassicSleep: true, Hibernate: false, HibernateFile: false));
        Assert.Equal("Classic sleep (S3), no hibernate", classic.Summary);
        Assert.Equal(HealthStatus.Info, classic.Status);

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
        Assert.Equal("Plugged in: boost off, no speed cap.\nOn battery: boost off, capped at 4000 MHz.", checks[0].Detail);
        Assert.Equal(HealthStatus.Info, checks[0].Status);
        Assert.Null(checks[0].Advice);
        Assert.Equal(
            [new HealthTableRow("Boost", "off", "off"), new HealthTableRow("Speed cap", "none", "4000 MHz")],
            checks[0].Table);

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
        Assert.Equal("exFAT", exfat.Summary);
        Assert.Contains("Xbox app", exfat.Detail);
        Assert.Contains("erases", exfat.Advice);
        Assert.Equal(HealthStatus.Info, exfat.Status);

        var ntfs = StorageHealth.CardCheck("E:", "NTFS");

        Assert.Equal("NTFS", ntfs.Summary);
        Assert.Null(ntfs.Advice);
    }

    private static HealthCheck Check(string name, HealthStatus status, string? summary = null, string detail = "detail") =>
        new() { Name = name, Detail = detail, Status = status, Summary = summary };

    [Fact]
    public void A_rows_group_and_title_come_out_of_its_name()
    {
        var check = Check("[AMD / Realtek] Windows Audio", HealthStatus.Good);

        Assert.Equal("AMD / Realtek", check.Group);
        Assert.Equal("Windows Audio", check.Title);

        var bare = Check("No group here", HealthStatus.Good);

        Assert.Equal(string.Empty, bare.Group);
        Assert.Equal("No group here", bare.Title);
    }

    /// <summary>A page like the Ally's: one service worth a look, the rest fine.</summary>
    private static readonly HealthCheck[] PageChecks =
    [
        Check("[ASUS] Armoury Crate Control Interface", HealthStatus.Warning, "Stopped", "Stopped, though it is set to start by itself"),
        Check("[ASUS] Armoury Crate Service", HealthStatus.Unknown, "Not installed"),
        Check("[Security] Windows Firewall", HealthStatus.Good, "Running"),
        Check("[Windows Update] Background Transfer (BITS)", HealthStatus.Good, "Resting"),
        Check("[Performance] Virtualisation-based security", HealthStatus.Good, detail: "Off"),
        Check("[Sleep] Wake timers", HealthStatus.Good, detail: "None"),
        Check("[Sleep] Sleeps by", HealthStatus.Info, "Modern Standby, hibernate ready"),
        new()
        {
            Name = "[Power plan] Turbo (in use)", Detail = "sentences", Status = HealthStatus.Info,
            Table = [new HealthTableRow("Boost", "off", "off")]
        },
        new()
        {
            Name = "[Power plan] Balanced", Detail = "sentences", Status = HealthStatus.Info,
            Table = [new HealthTableRow("Boost", "aggressive", "aggressive")]
        },
        Check("[Storage] Device encryption", HealthStatus.Info, "Off", "Anyone who takes the drive out can read it.")
    ];

    [Fact]
    public void Rows_that_need_a_look_are_lifted_to_the_top_worst_first()
    {
        HealthCheck[] checks = [.. PageChecks, Check("[Security] Microsoft Defender Antivirus", HealthStatus.Bad, "Disabled")];

        var page = HealthLayout.Build(checks, "note");

        Assert.Equal(["Microsoft Defender Antivirus", "Armoury Crate Control Interface"], page.Attention.Select(r => r.Title));
        Assert.Equal("Security", page.Attention[0].Tag);
        Assert.Equal(4, page.Fine);
    }

    [Fact]
    public void Sections_come_in_reading_order_with_the_protected_components_last()
    {
        var page = HealthLayout.Build(PageChecks, "note");

        Assert.Equal(
            ["Sleep", "Power plan", "Storage", "Performance", "Protected components"],
            page.Sections.Select(s => s.Title));
        Assert.Empty(HealthLayout.Build([], "note").Sections);
    }

    [Fact]
    public void The_protected_components_fold_into_one_line_that_still_says_what_needs_a_look()
    {
        var section = HealthLayout.Build(PageChecks, "the note").Sections[^1];

        Assert.Equal("1 to look at, 2 fine", section.Summary);
        Assert.Equal("the note", section.Note);
        Assert.True(section.CanToggle);
        Assert.False(section.ShowRows);
        Assert.Equal("Show all", section.ToggleText);
        Assert.Equal(4, section.Rows.Count);
        Assert.Equal("ASUS", section.Rows[0].Tag);

        section.ToggleCommand.Execute(null);

        Assert.True(section.ShowRows);
        Assert.Equal("Hide", section.ToggleText);
    }

    [Fact]
    public void A_section_with_nothing_wrong_says_nothing_and_shows_its_rows()
    {
        var sleep = HealthLayout.Build(PageChecks, "note").Sections[0];

        Assert.Equal(string.Empty, sleep.Summary);
        Assert.False(sleep.CanToggle);
        Assert.True(sleep.ShowRows);
        Assert.Equal(string.Empty, sleep.Rows[0].Tag);
    }

    [Fact]
    public void The_plan_in_use_is_shown_and_the_others_fold_away()
    {
        var plans = HealthLayout.Build(PageChecks, "note").Sections[1];

        Assert.Equal("Turbo (in use)", Assert.Single(plans.Tables).Heading);
        Assert.Equal("Balanced", Assert.Single(plans.MoreTables).Heading);
        Assert.Empty(plans.Rows);
        Assert.Equal("1 other plan", plans.ToggleText);
        Assert.Equal(ProcessorPowerReport.VendorNote, plans.Note);

        plans.IsExpanded = true;

        Assert.Equal("Hide other plans", plans.ToggleText);
    }

    [Fact]
    public void A_row_shows_its_short_answer_and_explains_only_when_it_is_not_simply_fine()
    {
        var fine = new HealthRowViewModel(Check("[Security] Windows Firewall", HealthStatus.Good, "Running", "Running, start type Automatic"));
        Assert.Equal("Running", fine.Value);
        Assert.Equal(string.Empty, fine.Note);

        var fact = new HealthRowViewModel(Check("[Storage] Device encryption", HealthStatus.Info, "Off", "Anyone can read it."));
        Assert.Equal("Off", fact.Value);
        Assert.Equal("Anyone can read it.", fact.Note);

        var plain = new HealthRowViewModel(Check("[Sleep] Wake timers", HealthStatus.Good, detail: "None"));
        Assert.Equal("None", plain.Value);
        Assert.Equal(string.Empty, plain.Note);

        var absent = new HealthRowViewModel(Check("[ASUS] Armoury Crate Service", HealthStatus.Unknown, "Not installed", "Not installed on this system"));
        Assert.Equal(string.Empty, absent.Note);
    }

    [Fact]
    public void Free_space_is_drawn_as_how_full_the_drive_is()
    {
        const long gb = 1024L * 1024 * 1024;

        var row = new HealthRowViewModel(StorageHealth.FreeSpaceCheck("C:", 250 * gb, 1000 * gb));

        Assert.True(row.HasBar);
        Assert.Equal(75, row.BarValue, 3);
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

        Assert.Equal(
            "Plugged in: power saving preference 0 of 100, every core kept awake.\n" +
            "On battery: power saving preference 60 of 100, 25% to 100% of cores kept awake.",
            check.Detail);
        Assert.Equal(
            [
                new HealthTableRow("Power saving preference", "0 of 100", "60 of 100"),
                new HealthTableRow("Cores kept awake", "all", "25% to 100%")
            ],
            check.Table);
    }

    [Theory]
    [InlineData("1|1", "On")]
    [InlineData("0|0", "Off")]
    [InlineData("0|1", "Not protected yet")]
    [InlineData("0|2", "Changing")]
    public void Encryption_is_reported_and_never_marked_as_something_to_fix(string status, string summary)
    {
        var check = StorageHealth.EncryptionCheck(status);

        Assert.Equal(summary, check.Summary);
        Assert.NotEmpty(check.Detail);
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
    [InlineData("Running", "Automatic", HealthStatus.Good)]
    [InlineData("Running", "Manual", HealthStatus.Good)]
    [InlineData("Stopped", "Manual", HealthStatus.Good)]       // BITS between downloads
    [InlineData("Stopped", "Automatic", HealthStatus.Warning)] // should be running and is not
    [InlineData("Stopped", "Disabled", HealthStatus.Bad)]
    [InlineData("Running", "Disabled", HealthStatus.Bad)]
    public void A_service_that_starts_on_demand_is_healthy_while_stopped(string status, string startType, HealthStatus expected)
    {
        Assert.Equal(expected, SystemStateService.ServiceStatus(status, startType));
    }

    [Theory]
    [InlineData("Stopped", "Disabled")]
    [InlineData("Running", "Disabled")]
    public void An_ASUS_service_off_for_Replace_Armoury_Crate_is_a_fact_with_no_advice(string status, string startType)
    {
        var check = SystemStateService.ServiceCheck(
            "ASUS App Service", "ASUS", status, startType, offForArmouryCrate: true);

        Assert.Equal(HealthStatus.Info, check.Status);
        Assert.Equal("Off while Handheld Optimiser replaces Armoury Crate", check.Summary);
        Assert.Null(check.Advice);
        Assert.Equal("[ASUS] ASUS App Service", check.Name);
    }

    [Fact]
    public void A_disabled_ASUS_service_is_still_a_fault_while_Armoury_Crate_is_not_replaced()
    {
        var check = SystemStateService.ServiceCheck(
            "ASUS App Service", "ASUS", "Stopped", "Disabled", offForArmouryCrate: false);

        Assert.Equal(HealthStatus.Bad, check.Status);
        Assert.Equal("Disabled", check.Summary);
        Assert.Equal("Stopped, start type Disabled", check.Detail);
        Assert.Equal(
            "This service is disabled. Handheld Optimiser never disables it, so another tool likely did. " +
            "Use Repair disabled services at the top of this page to switch it back on.",
            check.Advice);
    }

    [Theory]
    [InlineData("Running", "Automatic", "Running", "Running, start type Automatic")]
    [InlineData("Stopped", "Manual", "Resting", "Stopped until Windows needs it, start type Manual")]
    [InlineData("Stopped", "Automatic", "Stopped", "Stopped, though it is set to start by itself")]
    public void A_service_row_reads_as_before(string status, string startType, string summary, string detail)
    {
        var check = SystemStateService.ServiceCheck("Background Transfer (BITS)", "Windows Update", status, startType, offForArmouryCrate: false);

        Assert.Equal(summary, check.Summary);
        Assert.Equal(detail, check.Detail);
        Assert.Null(check.Advice);
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
