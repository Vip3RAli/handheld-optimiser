namespace HandheldOptimiser.PowerService;

/// <summary>
/// The USB product IDs of the ROG Ally controllers TDP Engine knows, all under ASUS's vendor ID 0B05.
/// 1ABE is the Ally (RC71) and 1B4C the Ally X (RC72), the IDs Linux's hid-asus driver uses for them.
/// The ROG Xbox Ally (RC73) is not listed: no public source gives its ID yet, and an ID added on a guess
/// would send this controller's commands to a device nobody has tried them on.
///
/// The main app does not build in this file, so <c>ArmouryCrateTweaks.AllyControllers</c> in
/// src\HandheldOptimiser\TweakDefinitions\ArmouryCrateTweaks.cs is its own copy, and a test keeps the two in step.
/// </summary>
internal static class AllyControllers
{
    /// <summary>ASUS's USB vendor ID.</summary>
    public const ushort VendorId = 0x0B05;

    /// <summary>The product IDs, the original Ally first.</summary>
    public static IReadOnlyList<ushort> ProductIds { get; } = [0x1ABE, 0x1B4C];

    /// <summary>
    /// Whether a device path or instance ID belongs to one of the controllers, as in
    /// <c>\\?\hid#vid_0b05&amp;pid_1abe&amp;mi_02&amp;col01#...</c>. Case does not matter.
    /// </summary>
    public static bool Matches(string? path) =>
        path is not null && ProductIds.Any(pid => path.Contains(HardwareId(pid), StringComparison.OrdinalIgnoreCase));

    /// <summary>The part of a hardware ID that names one controller, such as <c>VID_0B05&amp;PID_1ABE</c>.</summary>
    public static string HardwareId(ushort productId) => $"VID_{VendorId:X4}&PID_{productId:X4}";
}
