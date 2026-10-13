using System.Globalization;
using System.Text.RegularExpressions;

namespace HandheldOptimiser.Services;

/// <summary>A power plan as <c>powercfg /list</c> names it.</summary>
public sealed record PowerPlan(string Scheme, string Name, bool Active);

/// <summary>
/// The processor settings each power plan holds, shown and never changed. On a handheld the vendor's
/// tool owns these: a ROG Ally has one plan per Armoury Crate mode, and its Turbo plan already has
/// boost off and a 4000 MHz cap on battery. Anyone thinking of tuning them needs to see that first,
/// and a value written to one plan stops applying the moment the mode changes.
/// </summary>
public static partial class ProcessorPowerReport
{
    private const string Group = "[Power plan]";

    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";

    public const string BoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    public const string EnergyPreference = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    public const string MaxFrequency = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";
    public const string MinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    public const string MaxCores = "ea062031-0e34-4ff1-9b6d-eb1059334028";

    private const string InUseNote =
        "\nA vendor tool such as Armoury Crate writes these and changes plan with its performance mode, " +
        "so this app leaves them alone.";

    private const string GuidPattern ="[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}";

    // Plans and values are found by shape, as the Sleep & Battery tweaks do it: powercfg's labels are
    // translated, its GUIDs and trailing hex values are not. /qh also shows the settings Windows hides.
    private static readonly string Script =
        $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $guid = '{{GuidPattern}}'
        & powercfg.exe /getactivescheme | Select-String $guid | ForEach-Object { "ACTIVE=$($_.Matches[0].Value)" }
        $plans = @()
        foreach ($line in & powercfg.exe /list) {
            if ($line -match $guid) { $plans += $Matches[0]; Write-Output "LIST=$line" }
        }
        foreach ($plan in $plans) {
            & powercfg.exe /qh $plan {{ProcessorSubgroup}} | ForEach-Object { "QH=$plan=$_" }
        }
        """;

    public static async Task<IReadOnlyList<HealthCheck>> RunAsync(PowerShellRunner runner, CancellationToken ct)
    {
        var outcome = await runner.RunScriptAsync(Script, "Read the processor settings in each power plan", ct, echoScript: false, logOutput: false);
        return Checks(outcome.OutputLines);
    }

    [GeneratedRegex($@"({GuidPattern})\s+\((.*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex PlanLine();

    [GeneratedRegex(GuidPattern, RegexOptions.CultureInvariant)]
    private static partial Regex AnyGuid();

    [GeneratedRegex(@"0x([0-9a-fA-F]{8})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingHex();

    /// <summary>The plans from the LIST= and ACTIVE= lines, the one in use first.</summary>
    public static IReadOnlyList<PowerPlan> ParsePlans(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        var active = all.FirstOrDefault(l => l.StartsWith("ACTIVE=", StringComparison.Ordinal))?["ACTIVE=".Length..].Trim();

        return all
            .Where(l => l.StartsWith("LIST=", StringComparison.Ordinal))
            .Select(l => PlanLine().Match(l))
            .Where(m => m.Success)
            .Select(m => new PowerPlan(
                m.Groups[1].Value.ToLowerInvariant(),
                m.Groups[2].Value.Trim(),
                string.Equals(m.Groups[1].Value, active, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Active)
            .ToList();
    }

    /// <summary>
    /// One plan's settings from its QH= lines: each setting's GUID, then its values, of which the last
    /// two are the plugged-in and on-battery ones. A setting the plan does not report is simply absent.
    /// </summary>
    public static IReadOnlyDictionary<string, (uint Ac, uint Dc)> ParseSettings(IEnumerable<string> lines, string scheme)
    {
        var prefix = $"QH={scheme}=";
        var found = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        List<uint>? current = null;

        foreach (var line in lines.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(l => l[prefix.Length..]))
        {
            if (AnyGuid().Match(line) is { Success: true } guid)
            {
                found[guid.Value] = current = [];
            }
            else if (current is not null && TrailingHex().Match(line) is { Success: true } hex)
            {
                current.Add(uint.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        return found
            .Where(f => f.Value.Count >= 2)
            .ToDictionary(f => f.Key, f => (f.Value[^2], f.Value[^1]), StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<HealthCheck> Checks(IEnumerable<string> outputLines)
    {
        var lines = outputLines.ToList();
        var checks = new List<HealthCheck>();

        foreach (var plan in ParsePlans(lines))
        {
            var settings = ParseSettings(lines, plan.Scheme);
            var pluggedIn = Describe(settings, s => s.Ac);
            var onBattery = Describe(settings, s => s.Dc);

            if (pluggedIn.Length == 0)
            {
                continue;
            }

            checks.Add(new HealthCheck
            {
                Name = $"{Group} {plan.Name}{(plan.Active ? " (in use)" : string.Empty)}",
                // Said in the detail, not as advice: advice is drawn in yellow, and this is not a fault.
                Detail = $"Plugged in: {pluggedIn}.\nOn battery: {onBattery}." + (plan.Active ? InUseNote : string.Empty),
                Status = HealthStatus.Info
            });
        }

        if (checks.Count == 0)
        {
            checks.Add(new HealthCheck
            {
                Name = $"{Group} Processor settings",
                Detail = "Windows did not say",
                Status = HealthStatus.Unknown
            });
        }

        return checks;
    }

    private static string Describe(IReadOnlyDictionary<string, (uint Ac, uint Dc)> settings, Func<(uint Ac, uint Dc), uint> pick)
    {
        var parts = new List<string>();

        if (settings.TryGetValue(BoostMode, out var boost))
        {
            parts.Add($"boost {BoostName(pick(boost))}");
        }

        if (settings.TryGetValue(MaxFrequency, out var cap))
        {
            parts.Add(pick(cap) == 0 ? "no speed cap" : $"capped at {pick(cap)} MHz");
        }

        if (settings.TryGetValue(EnergyPreference, out var preference))
        {
            parts.Add($"power saving preference {pick(preference)} of 100");
        }

        if (settings.TryGetValue(MinCores, out var min) && settings.TryGetValue(MaxCores, out var max))
        {
            var (least, most) = (pick(min), pick(max));

            parts.Add(least >= 100 && most >= 100 ? "every core kept awake"
                : least == most ? $"{least}% of cores kept awake"
                : $"{least}% to {most}% of cores kept awake");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Windows' own names for the boost modes, in the order of their numbers.</summary>
    public static string BoostName(uint mode) => mode switch
    {
        0 => "off",
        1 => "on",
        2 => "aggressive",
        3 => "efficient",
        4 => "efficient aggressive",
        5 => "aggressive at guaranteed",
        6 => "efficient aggressive at guaranteed",
        _ => $"mode {mode}"
    };
}
