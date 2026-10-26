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

    // Set in a status read when this model has the device.
    private const int PresenceBit = 0x00010000;

    private const long Gibibyte = 1L << 30;

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

    /// <summary>
    /// The code in the firmware's answer to a status read, or null when this model does not have the device. A
    /// model without it answers 0xFFFFFFFE, which has the presence bit set too, so a negative answer counts as none.
    /// </summary>
    public static int? CodeFromStatus(int answer) => answer >= 0 && (answer & PresenceBit) != 0 ? answer & ~PresenceBit : null;

    /// <summary>
    /// The size Windows has now, from the memory the graphics driver reports as its own as it starts. Under 1 GB
    /// is Auto, which sets a little aside and shares more as games ask, as the Dashboard's tile reads it too.
    /// </summary>
    /// <returns>The size in GB, 0 for Auto, or null when the driver reported nothing.</returns>
    public static int? SizeFromBytes(long? bytes) => bytes is not { } reported
        ? null
        : reported < Gibibyte ? 0 : (int)Math.Round((double)reported / Gibibyte);

    /// <summary>The size Windows has now, and the size waiting for a restart, which is null when nothing waits.</summary>
    /// <param name="now">The size Windows has now, from <see cref="SizeFromBytes"/>, or null when not known.</param>
    /// <param name="firmwareCode">The firmware's code now, used from the next restart.</param>
    public static (int? Now, int? Next) Describe(int? now, int firmwareCode) =>
        (now, SizeOf(firmwareCode) is { } next && next != now ? next : null);
}
