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
        Disable8dot3Names
    ];

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
}
