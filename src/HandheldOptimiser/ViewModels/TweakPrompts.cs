using HandheldOptimiser.Models;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// When a tweak has to be confirmed. A tweak's warning used to be on show under it all the time. A
/// tile shows only a yellow triangle, with the warning one press away, so the warning is now put in
/// front of the player at the moment it matters: before the tweak is switched on.
/// </summary>
public static class TweakPrompts
{
    private static bool HasWarning(Tweak tweak) => !string.IsNullOrWhiteSpace(tweak.Warning);

    private static bool IsHighRisk(Tweak tweak) => tweak.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking;

    /// <summary>
    /// Whether switching the tweak on asks first: it carries a warning, it lowers security or breaks
    /// a feature, or it works around a fault on a device that is not this one.
    /// </summary>
    public static bool AsksFirst(Tweak tweak, bool isForThisDevice) =>
        !isForThisDevice || HasWarning(tweak) || IsHighRisk(tweak);

    /// <summary>The tweaks Apply all names in its confirmation, in the order they are on the page.</summary>
    public static IReadOnlyList<string> WarnedNames(IEnumerable<Tweak> pending) =>
        [.. pending.Where(t => HasWarning(t) || IsHighRisk(t)).Select(t => t.Name)];
}
