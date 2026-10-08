namespace HandheldOptimiser.HomeLauncher.Library;

public enum GameStore
{
    Steam,
    Xbox,
    Epic,
    BattleNet,
    Gog,
    Ea,
    Ubisoft,

    /// <summary>A program the player added to the library themselves.</summary>
    Other,

    /// <summary>A game file in one of the player's ROM folders, played in an emulator.</summary>
    Emulator
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
/// <param name="Platform">The console an emulator game is for, shown in place of the store's name.</param>
/// <param name="Installed">False for a game the player owns but has not installed. Its launch target
/// then opens the store's install page for it.</param>
/// <param name="CoverUrl">Where the store keeps a cover for a game that is not installed.</param>
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
    string? HeroPath = null,
    string? Platform = null,
    bool Installed = true,
    string? CoverUrl = null)
{
    public string StoreName => Platform ?? NameOf(Store);

    public static string NameOf(GameStore store) => store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Xbox => "Xbox",
        GameStore.Epic => "Epic Games",
        GameStore.BattleNet => "Battle.net",
        GameStore.Ea => "EA App",
        GameStore.Ubisoft => "Ubisoft Connect",
        GameStore.Other => "Added by you",
        GameStore.Emulator => "Emulators",
        _ => "GOG"
    };
}
