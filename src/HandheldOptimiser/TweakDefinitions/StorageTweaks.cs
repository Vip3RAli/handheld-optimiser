using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// NTFS metadata writes that cost SSD wear and small-file latency for no benefit on a gaming handheld.
/// Both only affect files touched or created from now on; nothing on disk is rewritten.
/// </summary>
public static class StorageTweaks
{
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";

    public static IReadOnlyList<Tweak> All =>
    [
        DisableLastAccess,
        Disable8dot3Names,
        ReservedStorage
    ];

    private const string ReservedStateScript =
        """
        try {
            $s = Get-WindowsReservedStorageState -ErrorAction Stop
            $v = "$($s.ReservedStorageState)"
            if (-not $v) { $v = ($s | Format-List | Out-String) }
            if ($v -match 'Disabled') { Write-Output 'RESERVED=Disabled' }
            elseif ($v -match 'Enabled') { Write-Output 'RESERVED=Enabled' }
            else { Write-Output 'RESERVED=Unknown' }
        } catch {
            Write-Output "Could not read reserved storage: $($_.Exception.Message)"
            Write-Output 'RESERVED=Unknown'
        }
        """;

    private static async Task<string> ReadReservedStateAsync(TweakContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.Runner.RunScriptAsync(ReservedStateScript, "Check reserved storage", ct, echoScript: false);
        var line = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("RESERVED=", StringComparison.Ordinal));
        return line?["RESERVED=".Length..] ?? "Unknown";
    }

    private static string SetReservedStateScript(bool enabled) =>
        $$"""
        try {
            Set-WindowsReservedStorageState -State {{(enabled ? "Enabled" : "Disabled")}} -ErrorAction Stop | Out-Null
            Write-Output 'Reserved storage {{(enabled ? "enabled" : "disabled")}}'
        } catch {
            Write-Output "failed: $($_.Exception.Message)"
            exit 1
        }
        """;

    /// <summary>
    /// Since Windows 10 1803 the value carries a "user managed" high bit: 0x80000001 is what
    /// <c>fsutil behavior set disablelastaccess 1</c> writes, and it stops Windows re-deciding the setting
    /// per volume size (the default 0x80000002 leaves it on for drives of 128 GB or less).
    /// </summary>
    public static Tweak DisableLastAccess => new()
    {
        Id = "storage.lastaccess",
        Name = "Disable last access timestamps",
        Description =
            "Stops NTFS writing a new timestamp every time a file is read. Game launches and shader loads read " +
            "thousands of files, so this removes a burst of small writes to the SSD each time.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, FileSystemKey, "NtfsDisableLastAccessUpdate",
                unchecked((int)0x80000001), RegistryValueKind.DWord)
        ]
    };

    public static Tweak Disable8dot3Names => new()
    {
        Id = "storage.8dot3",
        Name = "Disable 8.3 short file names",
        Description =
            "Stops NTFS generating a legacy DOS-style short name (PROGRA~1) alongside every new file. Speeds " +
            "up creating files in large folders such as game installs and shader caches.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "A few very old installers and 16-bit era tools rely on short names and may fail for files " +
            "created after this. Existing short names are kept.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, FileSystemKey, "NtfsDisable8dot3NameCreation", 1, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Windows holds back about 7 GB so updates always have room to install. This is a DISM setting, not
    /// part of Windows Update, and Windows refuses to change it while an update is mid-install.
    /// </summary>
    public static Tweak ReservedStorage => new()
    {
        Id = "storage.reserved",
        Name = "Turn off reserved storage",
        Description =
            "Gives back the space Windows keeps aside for updates, usually about 7 GB. Worth it on a 512 GB " +
            "drive where every game install counts.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "If the drive is nearly full when a large Windows update arrives, the update can fail to install " +
            "until you free some space. Windows will not change this setting while an update is installing; " +
            "restart and try again if it fails.",

        ScriptDetect = async (ctx, ct) => await ReadReservedStateAsync(ctx, ct) switch
        {
            "Disabled" => TweakState.Applied,
            "Enabled" => TweakState.NotApplied,
            _ => TweakState.Unknown
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            var state = await ReadReservedStateAsync(ctx, ct);
            if (state == "Disabled")
            {
                return TweakResult.NoChange("storage.reserved");
            }

            if (state != "Enabled")
            {
                return TweakResult.Fail("storage.reserved", "Could not read the reserved storage setting. See log.");
            }

            journal.CapturedState["reserved.state"] = state;

            var outcome = await ctx.Runner.RunScriptAsync(SetReservedStateScript(enabled: false), "Turn off reserved storage", ct);
            return outcome.Succeeded
                ? TweakResult.Ok("storage.reserved", "Reserved storage turned off.")
                : TweakResult.Fail("storage.reserved", "Windows would not turn off reserved storage. See log.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue("reserved.state", out var was) || was != "Enabled")
            {
                return TweakResult.NoChange("storage.reserved");
            }

            var outcome = await ctx.Runner.RunScriptAsync(SetReservedStateScript(enabled: true), "Turn reserved storage back on", ct);
            return outcome.Succeeded
                ? TweakResult.Ok("storage.reserved", "Reserved storage turned back on.")
                : TweakResult.Fail("storage.reserved", "Windows would not turn reserved storage back on. See log.");
        }
    };
}
