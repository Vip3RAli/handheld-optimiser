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
        ReservedStorage,
        SmallCrashDumps
    ];

    /// <summary>
    /// 3 is a small dump, 7 the automatic one Windows defaults to. With the automatic setting, a second
    /// crash within four weeks also makes Windows raise the page file to the size of the memory.
    /// </summary>
    public static Tweak SmallCrashDumps => new()
    {
        Id = "storage.crashdump",
        Name = "Keep crash dumps small",
        Description =
            "After a blue screen, Windows saves a small crash file instead of a memory dump that can " +
            "take several gigabytes.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "A small crash file holds less for tracking down a faulty driver. Undo this tweak if someone " +
            "helping you asks for a full memory dump.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CrashControl",
                "CrashDumpEnabled", 3, RegistryValueKind.DWord)
        ]
    };

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
        Name = "Turn off last access timestamps",
        Description =
            "Stops Windows writing to the SSD each time a file is read, to record when it was last opened.",
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
        Name = "Turn off short DOS-style file names",
        Description =
            "Stops NTFS creating a DOS-style short name (PROGRA~1) for every new file. Speeds up " +
            "creating files in large folders such as game installs.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "A few very old installers and 16-bit tools may fail for files created after this. Existing " +
            "short names are kept.",
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
            "Gives back the space Windows keeps aside for updates, usually about 7 GB.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "If the drive is nearly full, a large Windows update can fail until you free some space. " +
            "This setting cannot be changed while an update is installing; restart and try again if it " +
            "fails.",

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
                return TweakResult.Fail("storage.reserved", "Could not read the reserved storage setting. The console has the detail.");
            }

            journal.CapturedState["reserved.state"] = state;

            var outcome = await ctx.Runner.RunScriptAsync(SetReservedStateScript(enabled: false), "Turn off reserved storage", ct);
            return outcome.Succeeded
                ? TweakResult.Ok("storage.reserved", "Reserved storage turned off.")
                : TweakResult.Fail("storage.reserved", "Windows would not turn off reserved storage. The console has the detail.");
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
                : TweakResult.Fail("storage.reserved", "Windows would not turn reserved storage back on. The console has the detail.");
        }
    };
}
