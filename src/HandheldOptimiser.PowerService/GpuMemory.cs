namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Ally's GPU memory, the UMA frame buffer that Armoury Crate calls "Memory assigned to GPU": the sizes
/// on offer, the firmware's code for each, and what waits for a restart. Sizes are in GB, 0 for Auto. The
/// codes are those of Linux's asus-armoury driver; the sizes on offer are those Armoury Crate offers on the Ally.
/// </summary>
internal static class GpuMemory
{
    /// <summary>The firmware's device number for it, read and set through ATKACPI.</summary>
    public const uint Device = 0x000600C1;

    // How far apart two readings of when Windows started can be and still be the same start, as the clock is corrected now and then.
    private static readonly TimeSpan SameStart = TimeSpan.FromMinutes(2);

    // In the order offered. 8 GB's code comes between 4 and 5 GB's, out of order in the firmware too.
    private static readonly (int Size, int Code)[] Table =
    [
        (0, 0x100),
        (1, 0x102),
        (2, 0x103),
        (3, 0x104),
        (4, 0x105),
        (5, 0x107),
        (6, 0x108),
        (7, 0x109),
        (8, 0x106)
    ];

    /// <summary>The sizes on offer, in GB, Auto (0) first.</summary>
    public static readonly int[] Sizes = [.. Table.Select(t => t.Size)];

    /// <summary>The size a firmware code stands for, or null for a code not in the table, which is never guessed at.</summary>
    public static int? SizeOf(int code) => Table.Where(t => t.Code == code).Select(t => (int?)t.Size).FirstOrDefault();

    /// <summary>The firmware code for a size, or null for a size not on offer.</summary>
    public static int? CodeOf(int size) => Table.Where(t => t.Size == size).Select(t => (int?)t.Code).FirstOrDefault();

    /// <summary>The size Windows has now, and the size waiting for a restart, which is null when nothing waits.</summary>
    /// <param name="bootCode">The firmware's code when Windows started.</param>
    /// <param name="firmwareCode">The firmware's code now.</param>
    public static (int? Now, int? Next) Describe(int bootCode, int firmwareCode) =>
        (SizeOf(bootCode), firmwareCode == bootCode ? null : SizeOf(firmwareCode));

    /// <summary>The saved code Windows started with, when the note is from since Windows last started, or else null.</summary>
    /// <param name="savedStarted">When Windows had started as the note was saved, in UTC ticks.</param>
    public static int? BootCode(int? savedCode, long? savedStarted, DateTime started) =>
        savedCode is { } code && savedStarted is { } ticks && (new DateTime(ticks, DateTimeKind.Utc) - started).Duration() < SameStart
            ? code
            : null;
}
