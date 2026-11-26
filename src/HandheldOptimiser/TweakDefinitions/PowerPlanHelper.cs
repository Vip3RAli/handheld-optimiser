using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Shared helper for tweaks that read and write a powercfg setting across every power plan on the device.
/// Vendor tools switch plans with performance modes (Silent, Performance, Turbo), so each setting is
/// written to every plan and each plan's original value is journalled before changing it.
/// </summary>
internal static class PowerPlanHelper
{
    internal sealed record PlanValue(string Scheme, uint Ac, uint Dc);

    public static bool Matches(PlanValue plan, int? ac, int? dc) =>
        (ac is null || plan.Ac == ac) && (dc is null || plan.Dc == dc);

    /// <summary>
    /// Creates a tweak backed by one powercfg subgroup/setting GUID pair across all power plans.
    /// Leave <paramref name="ac"/> or <paramref name="dc"/> null to leave that power source untouched.
    /// </summary>
    public static Tweak CreateTweak(
        string id,
        string name,
        string description,
        TweakCategory category,
        RiskLevel risk,
        string? warning,
        bool includeInOneClick,
        string subgroup,
        string setting,
        int? ac,
        int? dc,
        HandheldDevice? onlyFor = null) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Category = category,
        Risk = risk,
        Warning = warning,
        OnlyFor = onlyFor,
        IncludeInOneClick = includeInOneClick,

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
            var plans = await ReadPlansAsync(ctx, subgroup, setting, ct);
            if (plans.Count == 0)
            {
                return TweakResult.Fail(id, "Could not read the power plans. The console has the detail.");
            }

            var commands = BuildApplyCommands(plans, journal, ac, dc);
            if (commands.Count == 0)
            {
                return TweakResult.NoChange(id);
            }

            return await WriteAsync(ctx, subgroup, setting, commands, $"Apply \"{name}\" to every power plan", ct)
                ? TweakResult.Ok(id, $"\"{name}\" applied.")
                : TweakResult.Fail(id, "One or more power plans could not be changed. The console has the detail.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            var commands = BuildRevertCommands(journal);
            if (commands.Count == 0)
            {
                return TweakResult.NoChange(id);
            }

            return await WriteAsync(ctx, subgroup, setting, commands, $"Restore power plans for \"{name}\"", ct)
                ? TweakResult.Ok(id, $"\"{name}\" undone.")
                : TweakResult.Fail(id, "One or more power plans could not be restored. The console has the detail.");
        }
    };

    public static List<string> BuildApplyCommands(List<PlanValue> plans, TweakJournalEntry journal, int? ac, int? dc)
    {
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

        return commands;
    }

    public static List<string> BuildRevertCommands(TweakJournalEntry journal)
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

        return commands;
    }

    /// <summary>
    /// The setting's current value in every power plan. The plan list and the values are picked out of
    /// powercfg's output by shape (GUIDs, and the trailing hex values) rather than by its labels, which
    /// are translated on non-English Windows. A plan that does not report the setting cleanly is left out.
    /// Asked with /qh, which also shows the settings Windows hides from its own power options.
    /// </summary>
    public static async Task<List<PlanValue>> ReadPlansAsync(TweakContext ctx, string subgroup, string setting, CancellationToken ct)
    {
        var outcome = await ctx.Runner.RunScriptAsync(
            $$"""
            $plans = & powercfg.exe /list | Select-String '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}' | ForEach-Object { $_.Matches[0].Value }
            foreach ($plan in $plans) {
                $hex = @(& powercfg.exe /qh $plan {{subgroup}} {{setting}} 2>$null | Select-String '(0x[0-9a-fA-F]{8})\s*$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
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

    public static async Task<bool> WriteAsync(
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
}
