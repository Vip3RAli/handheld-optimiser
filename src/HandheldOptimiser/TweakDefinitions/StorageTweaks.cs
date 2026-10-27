using Microsoft.Win32;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

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
        SmallCrashDumps,
        DeviceEncryption
    ];

    public static Tweak DeviceEncryption => new()
    {
        Id = "storage.deviceencryption",
        Name = "Turn off Device Encryption (BitLocker)",
        Description =
            "Turns off automatic device encryption on the system drive. Prevents BitLocker recovery key " +
            "lockouts after handheld BIOS updates and eliminates encryption write overhead.",
        Category = TweakCategory.Storage,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        Warning =
            "If your handheld is physically lost or stolen, data on the drive will not be encrypted. " +
            "Ensure game saves and important files are backed up to the cloud. Decrypting the SSD runs " +
            "in the background and may take some time.",

        ScriptDetect = async (ctx, ct) => await ReadBitLockerStateAsync(ctx, ct) switch
        {
            "Disabled" => TweakState.Applied,
            "Enabled" => TweakState.NotApplied,
            _ => TweakState.Unknown
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            var state = await ReadBitLockerStateAsync(ctx, ct);
            if (state == "Disabled")
            {
                return TweakResult.NoChange("storage.deviceencryption");
            }

            if (state != "Enabled")
            {
                return TweakResult.Fail("storage.deviceencryption", "Could not read the Device Encryption status. The console has the detail.");
            }

            journal.CapturedState["bitlocker.state"] = state;

            var outcome = await ctx.Runner.RunScriptAsync("manage-bde.exe -off $env:SystemDrive", "Turn off Device Encryption", ct);
            return outcome.Succeeded
                ? TweakResult.Ok("storage.deviceencryption", "Device encryption turned off. The drive is decrypting in the background.")
                : TweakResult.Fail("storage.deviceencryption", "Could not turn off device encryption. The console has the detail.");
        },

        // Encryption is turned back on from Settings, not with manage-bde. The Settings switch saves the
        // recovery key to the Microsoft account; manage-bde would encrypt the drive with no key saved
        // anywhere, and a BIOS update could then lock the player out for good.
        ScriptRevert = (ctx, journal, _) =>
        {
            if (!journal.CapturedState.TryGetValue("bitlocker.state", out var was) || was != "Enabled")
            {
                return Task.FromResult(TweakResult.NoChange("storage.deviceencryption"));
            }

            return Task.FromResult(ShellLinks.Open(DeviceEncryptionSettings, ctx.Log)
                ? TweakResult.Ok("storage.deviceencryption",
                    "Settings is open at Device encryption. Turn it on there, which also saves the recovery key to your Microsoft account.")
                : TweakResult.Fail("storage.deviceencryption",
                    "Could not open Settings. Turn Device encryption on in Settings, Privacy & security."));
        }
    };

    private const string DeviceEncryptionSettings = "ms-settings:deviceencryption";

    /// <summary>
    /// "Disabled" once the system drive is decrypted or decrypting, "Enabled" while it holds encrypted
    /// data (suspended protection included, as the data is still encrypted), and "Unknown" when the
    /// status cannot be read, so a failed read never shows the tweak as applied.
    /// </summary>
    private static async Task<string> ReadBitLockerStateAsync(TweakContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.Runner.RunScriptAsync(
            """
            try {
                $v = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop
                if ($v.VolumeStatus -eq 'FullyDecrypted' -or $v.VolumeStatus -eq 'DecryptionInProgress') {
                    Write-Output 'BITLOCKER=Disabled'
                } else {
                    Write-Output 'BITLOCKER=Enabled'
                }
            } catch {
                Write-Output 'BITLOCKER=Unknown'
            }
            """,
            "Read BitLocker volume status",
            ct,
            echoScript: false);

        var line = outcome.OutputLines.FirstOrDefault(l => l.StartsWith("BITLOCKER=", StringComparison.Ordinal));
        return line is not null ? line[10..] : "Unknown";
    }

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
