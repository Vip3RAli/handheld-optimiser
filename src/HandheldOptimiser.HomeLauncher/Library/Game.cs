namespace HandheldOptimiser.HomeLauncher.Library;

public enum GameStore
{
    Steam,
    Xbox,
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
/// <param name="InstallDirectory">Where the game's files are, for the quick actions menu.</param>
/// <param name="ExecutablePath">The game's own exe, when the store records it (Steam does not).</param>
/// <param name="HeroPath">Wide artwork for the library's background, when the store keeps one locally.</param>
public sealed record Game(
    string Key,
    string Title,
    GameStore Store,
    string? CoverPath,
    string? IconPath,
    string LaunchTarget,
    string? LaunchArguments = null,
    string? WorkingDirectory = null,
    string? InstallDirectory = null,
    string? ExecutablePath = null,
    string? HeroPath = null)
{
    public string StoreName => NameOf(Store);

    public static string NameOf(GameStore store) => store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Xbox => "Xbox",
        GameStore.Epic => "Epic Games",
        GameStore.BattleNet => "Battle.net",
        _ => "GOG"
    };
}
