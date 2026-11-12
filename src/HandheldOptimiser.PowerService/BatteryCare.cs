namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Ally's battery charge limit, which Armoury Crate calls "Battery Care Mode": stopping at 80% while
/// plugged in so a battery left on mains power wears much less over time. The firmware resets the limit
/// to 100% when the handheld restarts, so the saved setting is kept in the registry and put back by the
/// service when it starts or wakes from sleep.
/// </summary>
internal static class BatteryCare
{
    /// <summary>The firmware's device number for the battery charge limit, set through ATKACPI.</summary>
    public const uint Device = 0x00120057;

    /// <summary>Where the saved limit is kept in HKLM so the service and the main app share it.</summary>
    public const string SettingsKey = @"SOFTWARE\HandheldOptimiser\BatteryCare";
    public const string LimitValue = "Limit";

    public const int CareLimit = 80;
    public const int FullLimit = 100;

    /// <summary>The percentage to send to the firmware for a toggle state.</summary>
    public static int LimitFor(bool on) => on ? CareLimit : FullLimit;

    /// <summary>Whether a saved registry value means the 80% charge limit is on.</summary>
    public static bool IsEnabled(object? saved) => saved is int limit && limit == CareLimit;

    /// <summary>
    /// Whether TDP Engine should start with Windows. The firmware forgets the limit at a restart, and the
    /// service otherwise only starts when asked, so while the limit is on it has to start by itself to put it
    /// back. Replacing Armoury Crate needs it running from the start too, so that is never undone here.
    /// </summary>
    public static bool StartsWithWindows(bool careOn, bool replacing) => careOn || replacing;
}
