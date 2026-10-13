using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HandheldOptimiser.TweakDefinitions;

namespace HandheldOptimiser.Services;

/// <summary>Which sleep states the firmware offers, and whether hibernate is switched on.</summary>
public sealed record SleepStates(bool ModernStandby, bool ClassicSleep, bool Hibernate, bool HibernateFile);

/// <summary>One stretch asleep from Windows' sleep study. The drain is null where no battery reading was kept.</summary>
public sealed record SleepSession(string Type, bool OnBattery, double Hours, double? DrainPercent);

/// <summary>Something holding the device awake: a driver, a program or a service, and its reason.</summary>
public sealed record PowerRequest(string Kind, string Who, string Why);

/// <summary>
/// What Windows itself reports about sleep: the states the device has, how much battery sleep has
/// used, and what can keep it awake or wake it. Sleep is the loudest complaint about Windows handhelds
/// and the cause differs by device, so this shows the evidence in place of guessing at a fix. Nothing
/// here changes anything.
///
/// powercfg's text is translated on non-English Windows, so nothing is matched on its wording: entries
/// are found by their shape (the [DRIVER], [PROCESS] and [SERVICE] tags, which stay in English) and
/// shown as Windows printed them. The sleep study is read as XML for the same reason.
/// </summary>
public static partial class SleepHealth
{
    private const string Group = "[Sleep]";

    /// <summary>How far back the sleep study looks.</summary>
    public const int StudyDays = 7;

    /// <summary>Shorter sleeps say little: the battery gauge does not move enough to read a rate from.</summary>
    public const double ShortestSleepHours = 10.0 / 60;

    /// <summary>
    /// Percent of the battery an hour. A healthy Modern Standby uses well under this; a handheld with
    /// something keeping it busy in a bag uses several times as much.
    /// </summary>
    public const double HighDrainPerHour = 1.0;

    // The sleep study goes to a file, because powercfg can write it nowhere else. Windows\Temp, not the
    // user's own temp folder: this runs elevated, and a file there could be swapped by anything running
    // as the user before it is read back.
    private static readonly string Script =
        $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        & powercfg.exe /requests | ForEach-Object { "REQ=$_" }
        & powercfg.exe /waketimers | ForEach-Object { "TIMER=$_" }
        & powercfg.exe /lastwake | ForEach-Object { "WAKE=$_" }
        {{SleepTweaks.ListArmedScript}}
        Write-Output 'ARMED_ASKED'
        $file = Join-Path $env:SystemRoot ('Temp\ho-sleepstudy-' + [guid]::NewGuid().ToString('N') + '.xml')
        & powercfg.exe /sleepstudy /output $file /xml /duration {{StudyDays}} | Out-Null
        if (Test-Path -LiteralPath $file) {
            try {
                [xml]$study = Get-Content -LiteralPath $file -Raw
                Write-Output 'STUDY_READ'
                foreach ($s in $study.SleepStudy.ScenarioInstances.OsStateInstance) {
                    Write-Output ("SESSION={0}|{1}|{2}|{3}|{4}|{5}" -f $s.Type, $s.Ac, $s.Duration, $s.EntryRemainingCapacity, $s.ExitRemainingCapacity, $s.EntryFullCapacity)
                }
            } catch { }
            Remove-Item -LiteralPath $file -Force
        }
        """;

    public static async Task<IReadOnlyList<HealthCheck>> RunAsync(PowerShellRunner runner, CancellationToken ct)
    {
        // Not echoed line by line: a week of sessions is a hundred lines, at every launch.
        var outcome = await runner.RunScriptAsync(Script, "Ask Windows about sleep", ct, echoScript: false, logOutput: false);
        var lines = outcome.OutputLines.ToList();

        IEnumerable<string> After(string prefix) => lines
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..]);

        return
        [
            SleepStatesCheck(ReadSleepStates()),
            BatteryAsleepCheck(lines.Contains("STUDY_READ") ? ParseSessions(After("SESSION=")) : null),
            RequestsCheck(ParseRequests(After("REQ="))),
            WakeTimersCheck(ParseWakeTimers(After("TIMER="))),
            WakeDevicesCheck(lines.Contains("ARMED_ASKED") ? After("DEV=").Select(d => d.Trim()).Where(d => d.Length > 0).ToList() : null),
            LastWakeCheck(After("WAKE="))
        ];
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

    /// <summary>
    /// Asked of Windows directly, where <c>powercfg /a</c> answers in translated sentences. The bytes
    /// are SYSTEM_POWER_CAPABILITIES, which is 76 bytes of one-byte flags at fixed places.
    /// </summary>
    private static SleepStates? ReadSleepStates()
    {
        var caps = new byte[128];

        try
        {
            if (!GetPwrCapabilities(caps))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        return new SleepStates(
            ModernStandby: caps[20] != 0,
            ClassicSleep: caps[5] != 0,
            Hibernate: caps[6] != 0,
            HibernateFile: caps[8] != 0);
    }

    public static HealthCheck SleepStatesCheck(SleepStates? states)
    {
        const string name = $"{Group} Sleeps by";

        if (states is null)
        {
            return new HealthCheck { Name = name, Detail = "Windows did not say", Status = HealthStatus.Unknown };
        }

        // Whether the network stays on in Modern Standby is a power plan setting, not something the
        // device can or cannot do, and Turn Wi-Fi off during sleep on the Sleep & Battery page shows it.
        // The short answer names the states. The sentence under it says what that means for the battery,
        // so the two never repeat each other.
        var meaning = states.ModernStandby && states.HibernateFile
            ? "Asleep, part of the system keeps running and uses the battery. Hibernating uses none."
            : states.ModernStandby
                ? "Asleep, part of the system keeps running and uses the battery, and there is no hibernate to fall back on."
                : states.ClassicSleep
                    ? "Asleep, the handheld uses almost no power."
                    : "Windows reports no way for this device to sleep.";

        var summary =
            (states.ModernStandby ? "Modern Standby" : states.ClassicSleep ? "Classic sleep (S3)" : "No sleep state") +
            (!states.Hibernate ? ", no hibernate" : states.HibernateFile ? ", hibernate ready" : ", hibernate off");

        // Modern Standby with no hibernate to fall back on is the case that empties a battery in a bag.
        var exposed = states.ModernStandby && states.Hibernate && !states.HibernateFile;

        return new HealthCheck
        {
            Name = name,
            Detail = meaning,
            Summary = summary,
            Status = exposed ? HealthStatus.Warning : HealthStatus.Info,
            Advice = exposed
                ? "Sleep is the only rest this handheld gets, and it keeps using the battery. Hibernate " +
                  "after 15 minutes of sleep, on the Sleep & Battery page, switches hibernate on."
                : null
        };
    }

    /// <summary>
    /// Sessions as the script prints them: type, on mains (1 or 0), length in 100 nanosecond ticks, then
    /// the battery's charge going in, coming out and when full, in mWh. A line that does not fit is skipped.
    /// </summary>
    public static IReadOnlyList<SleepSession> ParseSessions(IEnumerable<string> lines)
    {
        var sessions = new List<SleepSession>();

        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 6)
            {
                continue;
            }

            var numbers = parts[^5..];
            if (!double.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ticks)
                || !double.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var entry)
                || !double.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var exit)
                || !double.TryParse(numbers[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var full))
            {
                continue;
            }

            // A reading of 0 is Windows having no reading, not an empty battery.
            double? drain = entry > 0 && exit > 0 && full > 0 ? (entry - exit) / full * 100 : null;

            sessions.Add(new SleepSession(
                Type: string.Join('|', parts[..^5]).Trim(),
                OnBattery: numbers[0].Trim() == "0",
                Hours: ticks / TimeSpan.TicksPerHour,
                DrainPercent: drain));
        }

        return sessions;
    }

    /// <summary>
    /// The session types that are sleep with the system still powered. Only "Screen Off" has been seen,
    /// on a ROG Ally on English Windows; the others are the names Microsoft's own report uses. A type
    /// this does not know is left out, so an unfamiliar Windows reads as "no sleeps", never as a wrong figure.
    /// </summary>
    private static bool IsSleep(string type) =>
        type.Equals("Screen Off", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Sleep", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Standby", StringComparison.OrdinalIgnoreCase);

    public static HealthCheck BatteryAsleepCheck(IReadOnlyList<SleepSession>? sessions)
    {
        const string name = $"{Group} Battery while asleep";

        if (sessions is null)
        {
            return new HealthCheck { Name = name, Detail = "Windows gave no sleep study", Status = HealthStatus.Unknown };
        }

        var sleeps = sessions
            .Where(s => s.OnBattery && IsSleep(s.Type) && s.Hours >= ShortestSleepHours && s.DrainPercent >= 0)
            .ToList();

        if (sleeps.Count == 0)
        {
            return new HealthCheck
            {
                Name = name,
                Detail = $"No sleep of 10 minutes or more on battery in the last {StudyDays} days.",
                Summary = "Nothing to measure yet",
                Status = HealthStatus.Info
            };
        }

        var hours = sleeps.Sum(s => s.Hours);
        var drain = sleeps.Sum(s => s.DrainPercent ?? 0);
        var rate = drain / hours;
        var high = rate >= HighDrainPerHour;

        return new HealthCheck
        {
            Name = name,
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"On battery in the last {StudyDays} days: asleep for {hours:0.#} hours over {sleeps.Count} " +
                $"sleep{(sleeps.Count == 1 ? "" : "s")}, using {drain:0.#}% of the battery ({rate:0.0}% an hour)."),
            Summary = string.Create(CultureInfo.InvariantCulture, $"{rate:0.0}% an hour, over {hours:0.#} hours asleep"),
            Status = high ? HealthStatus.Warning : HealthStatus.Good,
            Advice = high
                ? "That is a lot for a device that is asleep. The two hibernate settings on the Sleep & " +
                  "Battery page stop it, and the rows below show what was keeping it busy."
                : null
        };
    }

    [GeneratedRegex(@"^\[(DRIVER|PROCESS|SERVICE)\]", RegexOptions.CultureInvariant)]
    private static partial Regex RequesterTag();

    [GeneratedRegex(@"\[(DRIVER|PROCESS|SERVICE)\]", RegexOptions.CultureInvariant)]
    private static partial Regex AnyRequesterTag();

    [GeneratedRegex(@"^[A-Z]+:$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestHeading();

    /// <summary>
    /// <c>powercfg /requests</c>: a heading per kind (DISPLAY:, SYSTEM: and so on), then for each
    /// request a tagged line naming who asked and a line saying why. "None." under a heading is
    /// translated, which is why it is not looked for.
    /// </summary>
    public static IReadOnlyList<PowerRequest> ParseRequests(IEnumerable<string> lines)
    {
        var requests = new List<PowerRequest>();
        var kind = string.Empty;
        string? who = null;

        void Close(string why)
        {
            if (who is not null)
            {
                requests.Add(new PowerRequest(kind, who, why));
                who = null;
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (RequestHeading().IsMatch(line))
            {
                Close(string.Empty);
                kind = line.TrimEnd(':');
            }
            else if (RequesterTag().IsMatch(line))
            {
                Close(string.Empty);
                who = line;
            }
            else if (line.Length > 0)
            {
                Close(line);
            }
        }

        Close(string.Empty);
        return requests;
    }

    public static HealthCheck RequestsCheck(IReadOnlyList<PowerRequest> requests)
    {
        const string name = $"{Group} Keeping it awake now";

        if (requests.Count == 0)
        {
            return new HealthCheck { Name = name, Detail = "Nothing", Status = HealthStatus.Good };
        }

        return new HealthCheck
        {
            Name = name,
            Detail = string.Join("\n", requests.Select(r => r.Why.Length > 0 ? $"{Short(r.Who)}: {r.Why}" : Short(r.Who))),
            Summary = requests.Count == 1 ? "1 thing" : $"{requests.Count} things",
            Status = HealthStatus.Warning,
            Advice =
                "A game, video or download does this while it runs, which is normal. Anything still " +
                "listed with nothing playing is why the handheld will not go to sleep by itself."
        };
    }

    /// <summary>A program is named by its file, not by the device path Windows prints in front of it.</summary>
    private static string Short(string requester)
    {
        if (!requester.StartsWith("[PROCESS]", StringComparison.Ordinal))
        {
            return requester;
        }

        var slash = requester.LastIndexOf('\\');
        return slash < 0 ? requester : $"[PROCESS] {requester[(slash + 1)..]}";
    }

    /// <summary>
    /// <c>powercfg /waketimers</c>: each timer is a line naming the program or service that set it,
    /// usually followed by a line giving the reason. Both are kept as Windows printed them.
    /// </summary>
    public static IReadOnlyList<string> ParseWakeTimers(IEnumerable<string> lines)
    {
        var timers = new List<string>();
        var open = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (AnyRequesterTag().IsMatch(line))
            {
                timers.Add(line);
                open = true;
            }
            else if (open && line.Length > 0)
            {
                timers[^1] = $"{timers[^1]} {line}";
                open = false;
            }
        }

        return timers;
    }

    public static HealthCheck WakeTimersCheck(IReadOnlyList<string> timers)
    {
        const string name = $"{Group} Wake timers";

        if (timers.Count == 0)
        {
            return new HealthCheck { Name = name, Detail = "None", Status = HealthStatus.Good };
        }

        return new HealthCheck
        {
            Name = name,
            Detail = string.Join("\n", timers),
            Summary = timers.Count == 1 ? "1 timer" : $"{timers.Count} timers",
            Status = HealthStatus.Warning,
            Advice = "A wake timer switches the handheld on by itself at that time, in a bag as well."
        };
    }

    /// <param name="devices">The devices allowed to wake it, or null when Windows was not asked.</param>
    public static HealthCheck WakeDevicesCheck(IReadOnlyList<string>? devices)
    {
        const string name = $"{Group} Can wake it";

        if (devices is null)
        {
            return new HealthCheck { Name = name, Detail = "Windows did not say", Status = HealthStatus.Unknown };
        }

        if (devices.Count == 0)
        {
            return new HealthCheck { Name = name, Detail = "Only the power button", Status = HealthStatus.Good };
        }

        return new HealthCheck
        {
            Name = name,
            Detail = string.Join(", ", devices),
            Summary = devices.Count == 1 ? "1 device besides the power button" : $"{devices.Count} devices besides the power button",
            Status = HealthStatus.Warning,
            Advice =
                "A bump on any of these wakes the handheld in a bag. Only the power button wakes the " +
                "handheld, on the Sleep & Battery page, stops them."
        };
    }

    /// <summary>
    /// <c>powercfg /lastwake</c>, shown as Windows printed it: the lines that give a label and a value.
    /// The count lines above them use a dash, not a colon, and are left out.
    /// </summary>
    public static HealthCheck LastWakeCheck(IEnumerable<string> lines)
    {
        const string name = $"{Group} Last woken by";

        var facts = lines
            .Select(l => l.Trim())
            .Where(l => l.Contains(": ", StringComparison.Ordinal) && !l.EndsWith(':'))
            .ToList();

        return new HealthCheck
        {
            Name = name,
            Detail = facts.Count == 0 ? "Windows has no record of it" : string.Join("\n", facts),
            Status = HealthStatus.Info
        };
    }
}
