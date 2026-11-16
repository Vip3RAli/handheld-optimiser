namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What a run of one of the library's installers needs from the queue, and tells it as it goes.</summary>
internal sealed class InstallJob
{
    /// <summary>The store the game is from, which says which installer runs it.</summary>
    public GameStore Store { get; init; } = GameStore.Gog;

    /// <summary>The store's id for the game: GOG's product id, or Epic's app name.</summary>
    public required string ProductId { get; init; }

    public required string Title { get; init; }

    public string? Cover { get; init; }

    public InstallMode Mode { get; init; }

    /// <summary>An Epic game's namespace and catalogue id, which its build is asked for with.</summary>
    public string? Namespace { get; init; }

    public string? CatalogItemId { get; init; }

    /// <summary>The game's folder, once known.</summary>
    public string? Folder { get; set; }

    public long Total;
    public long Done;

    /// <summary>Checking the files already there, for a check of the game's files, before anything is fetched.</summary>
    public bool Checking { get; set; }
}

/// <summary>An install that cannot go on, with why, for the player. A lost connection is not one: that is tried again.</summary>
internal sealed class InstallException(string message) : Exception(message);
