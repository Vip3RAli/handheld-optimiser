namespace HandheldOptimiser.HomeLauncher.Library;

public enum GameStore
{
    Steam,
    Epic,
    BattleNet,
    Gog
}

/// <summary>
/// An installed game and how to start it. <see cref="Key"/> is stable across scans, so it is what the
/// launch history is stored against.
/// </summary>
/// <param name="CoverPath">Portrait artwork, when the store keeps one locally (only Steam does).</param>
/// <param name="IconPath">Exe or .ico to take an icon from when there is no cover.</param>
/// <param name="LaunchTarget">Passed to shell execute: a URI or an exe.</param>
public sealed record Game(
    string Key,
    string Title,
    GameStore Store,
    string? CoverPath,
    string? IconPath,
    string LaunchTarget,
    string? LaunchArguments = null,
    string? WorkingDirectory = null)
{
    public string StoreName => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.BattleNet => "Battle.net",
        _ => "GOG"
    };
}
