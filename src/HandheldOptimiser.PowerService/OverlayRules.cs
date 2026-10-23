namespace HandheldOptimiser.PowerService;

/// <summary>What a set-overlay request comes to, with no Windows calls, so it can be tested.</summary>
internal static class OverlayRules
{
    /// <summary>The level a request asks for, or the current one when it names none or one not known.</summary>
    public static string Level(string? asked, string current) => Pick(asked, current, AutoTdpProtocol.OverlayLevels);

    /// <summary>The corner a request asks for, or the current one when it names none or one not known.</summary>
    public static string Corner(string? asked, string current) => Pick(asked, current, AutoTdpProtocol.OverlayCorners);

    /// <summary>The layout a request asks for, or the current one when it names none or one not known.</summary>
    public static string Layout(string? asked, string current) => Pick(asked, current, AutoTdpProtocol.OverlayLayouts);

    /// <summary>A saved layout as it is, or vertical when it is missing or not one of the layouts.</summary>
    public static string SavedLayout(string? saved) => Pick(saved, AutoTdpProtocol.OverlayVertical, AutoTdpProtocol.OverlayLayouts);

    /// <summary>A saved level as it is, or Off when it is missing or not one of the levels.</summary>
    public static string SavedLevel(string? saved) => Pick(saved, AutoTdpProtocol.OverlayOff, AutoTdpProtocol.OverlayLevels);

    /// <summary>A saved corner as it is, or top left when it is missing or not one of the corners.</summary>
    public static string SavedCorner(string? saved) => Pick(saved, AutoTdpProtocol.TopLeft, AutoTdpProtocol.OverlayCorners);

    private static string Pick(string? asked, string fallback, string[] known) =>
        asked is not null && known.Contains(asked, StringComparer.Ordinal) ? asked : fallback;
}
