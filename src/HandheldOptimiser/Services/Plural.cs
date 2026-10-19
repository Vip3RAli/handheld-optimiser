namespace HandheldOptimiser.Services;

/// <summary>Builds a count with the right singular or plural noun, so the UI never says "1 tweaks" or "tweak(s)".</summary>
public static class Plural
{
    /// <summary>
    /// "1 tweak" or "3 tweaks". <paramref name="many"/> is the plural form when adding "s" is not enough,
    /// for example "1 child" and "2 children".
    /// </summary>
    public static string Count(int n, string one, string? many = null) =>
        n == 1 ? $"{n} {one}" : $"{n} {many ?? one + "s"}";
}
