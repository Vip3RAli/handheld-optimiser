using System.Globalization;
using System.IO;

namespace HandheldOptimiser.Services;

/// <summary>
/// Three facts about storage that explain problems people otherwise chase elsewhere: a nearly full
/// drive, a card the Xbox app will not install to, and whether the drive is encrypted. Each is shown
/// with a way to the Windows page that changes it. Nothing is changed here, and encryption least of
/// all: a handheld is the PC most likely to be left on a train.
/// </summary>
public static class StorageHealth
{
    private const string Group = "[Storage]";

    /// <summary>Below this much free space, Windows has little room to grow its page file under a game.</summary>
    public const long LowFreeBytes = 20L * 1024 * 1024 * 1024;

    private const string Script =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        $v = Get-CimInstance -Namespace root\CIMV2\Security\MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume |
            Where-Object { $_.DriveLetter -eq $env:SystemDrive } | Select-Object -First 1
        if ($v) { Write-Output ("ENC={0}|{1}" -f $v.ProtectionStatus, $v.ConversionStatus) }
        """;

    public static async Task<IReadOnlyList<HealthCheck>> RunAsync(PowerShellRunner runner, CancellationToken ct)
    {
        var outcome = await runner.RunScriptAsync(Script, "Read the encryption state", ct, echoScript: false, logOutput: false);

        return
        [
            SystemDriveCheck(),
            .. CardChecks(),
            EncryptionCheck(outcome.OutputLines.FirstOrDefault(l => l.StartsWith("ENC=", StringComparison.Ordinal))?["ENC=".Length..])
        ];
    }

    /// <summary>
    /// One row per card in the reader, and none when it is empty. Read through .NET: Get-Volume gives
    /// the same answer but takes two seconds to load on a ROG Ally.
    /// </summary>
    private static List<HealthCheck> CardChecks()
    {
        var checks = new List<HealthCheck>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady))
            {
                checks.Add(CardCheck(drive.Name.TrimEnd('\\'), drive.DriveFormat));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A card pulled out part way through. It has no row, which is true by then.
        }

        return checks;
    }

    private static HealthCheck SystemDriveCheck()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        try
        {
            var drive = new DriveInfo(root);
            return FreeSpaceCheck(root.TrimEnd('\\'), drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HealthCheck
            {
                Name = $"{Group} Free space on {root.TrimEnd('\\')}",
                Detail = "Windows did not say",
                Status = HealthStatus.Unknown
            };
        }
    }

    public static HealthCheck FreeSpaceCheck(string drive, long freeBytes, long totalBytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        var low = freeBytes < LowFreeBytes;

        return new HealthCheck
        {
            Name = $"{Group} Free space on {drive}",
            Detail = string.Create(CultureInfo.InvariantCulture, $"{freeBytes / gb:0} GB free of {totalBytes / gb:0} GB"),
            Status = low ? HealthStatus.Warning : HealthStatus.Good,
            Advice = low
                ? "When a game needs more memory than the handheld has, Windows makes room on this drive. " +
                  "With little free it cannot, and games can close with \"out of memory\" errors. Updates " +
                  "need room too."
                : null,
            OpenText = low ? "Open Storage" : null,
            OpenUri = low ? "ms-settings:storagesense" : null
        };
    }

    /// <summary>A card's row, from its drive ("D:") and the file system it is formatted with.</summary>
    public static HealthCheck CardCheck(string drive, string fileSystem)
    {
        var ntfs = fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

        return new HealthCheck
        {
            Name = $"{Group} Card in {drive}",
            Detail = ntfs
                ? "NTFS. Every launcher can install games to it."
                : $"{fileSystem}. The Xbox app only installs games to cards formatted as NTFS. Steam and the other launchers work as it is.",
            Status = HealthStatus.Info,
            Advice = ntfs ? null : "Formatting a card erases it, so copy off anything you want to keep first."
        };
    }

    /// <summary>
    /// From Windows' own two numbers for the system drive. Protection: 0 off, 1 on. Conversion: 0 not
    /// encrypted, 1 encrypted, 2 and above part way. A drive can be encrypted with protection still
    /// off, which is how Windows leaves it until someone signs in with a Microsoft account.
    /// </summary>
    public static HealthCheck EncryptionCheck(string? status)
    {
        const string name = $"{Group} Device encryption";

        var parts = status?.Split('|') ?? [];
        var protection = parts.Length == 2 ? parts[0].Trim() : null;
        var conversion = parts.Length == 2 ? parts[1].Trim() : null;

        var detail = (protection, conversion) switch
        {
            ("1", _) =>
                "On. If the handheld is lost or stolen, nobody can read the drive or the logins saved on it. " +
                "It costs some drive speed.",
            ("0", "0") =>
                "Off. Anyone who takes the drive out can read it, saved logins included.",
            ("0", "1") =>
                "Encrypted, but not protected yet. Windows finishes switching it on when you sign in with a Microsoft account.",
            ("0", _) =>
                "Part way through being switched on or off.",
            _ => null
        };

        return detail is null
            ? new HealthCheck { Name = name, Detail = "Windows did not say", Status = HealthStatus.Unknown }
            : new HealthCheck
            {
                Name = name,
                Detail = detail,
                Status = HealthStatus.Info,
                OpenText = "Open device encryption",
                OpenUri = "ms-settings:deviceencryption"
            };
    }
}
