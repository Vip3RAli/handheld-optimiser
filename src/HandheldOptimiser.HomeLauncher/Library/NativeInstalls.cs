using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A game the library downloaded and installed itself, as its own record keeps it.</summary>
/// <param name="Exe">The game's exe, relative to <paramref name="Folder"/>.</param>
/// <param name="WorkingDirectory">Where the game starts, relative to <paramref name="Folder"/>, or null for the exe's own folder.</param>
internal sealed record NativeInstall(
    GameStore Store,
    string Id,
    string Title,
    string BuildId,
    string Version,
    string Folder,
    string Exe,
    string? Arguments,
    string? WorkingDirectory,
    long Size,
    DateTimeOffset InstalledAt,
    string? Cover = null,
    string? UpdateBuildId = null,
    string? Namespace = null,
    string? CatalogItemId = null)
{
    public string Key => $"{Store.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>
/// The library's own record of the games it installed (GOG and Epic), in
/// <c>%LOCALAPPDATA%\HandheldOptimiser\installs</c>. Games installed this way are in no store app's files and
/// GOG's registry keys need administrator, so this record is how the library finds them again.
/// </summary>
internal static class NativeInstalls
{
    private static readonly Lock Gate = new();

    internal static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "installs");

    /// <summary>Where the record and each install's working files are kept. Tests point it somewhere of their own.</summary>
    internal static string Folder { get; set; } = DefaultFolder;

    private static string RecordPath => Path.Combine(Folder, "installed.json");

    /// <summary>Every game in the record.</summary>
    public static List<NativeInstall> All()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(RecordPath) ? JsonSerializer.Deserialize<List<NativeInstall>>(File.ReadAllText(RecordPath)) ?? [] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Program.Log($"Could not read the library's installs: {ex.GetType().Name}");
                return [];
            }
        }
    }

    /// <summary>The record's entry for a game, by its key.</summary>
    public static NativeInstall? Find(string key) => All().FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a game to the record, or replaces its entry.</summary>
    public static void Save(NativeInstall install) => Change(list =>
    {
        list.RemoveAll(i => i.Key == install.Key);
        list.Add(install);
    });

    /// <summary>Updates <see cref="NativeInstall.UpdateBuildId"/> only if the entry still exists with the same build.</summary>
    public static void SetUpdateBuildId(string key, string expectedBuildId, string? updateBuildId) => Change(list =>
    {
        var index = list.FindIndex(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase) && i.BuildId == expectedBuildId);
        if (index >= 0 && list[index].UpdateBuildId != updateBuildId)
        {
            list[index] = list[index] with { UpdateBuildId = updateBuildId };
        }
    });

    /// <summary>Takes a game out of the record.</summary>
    public static void Remove(string key) => Change(list => list.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)));

    private static void Change(Action<List<NativeInstall>> change)
    {
        lock (Gate)
        {
            var list = All();
            change(list);
            var temp = $"{RecordPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(temp, JsonSerializer.Serialize(list));
                File.Move(temp, RecordPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                Program.Log($"Could not keep the library's installs: {ex.Message}");
            }
        }
    }

    // ----- Each install's working files -----

    private static string Prefix(GameStore store) => store.ToString().ToLowerInvariant();

    /// <summary>Where an install keeps how far it has got.</summary>
    internal static string StatePath(GameStore store, string id) => Path.Combine(Folder, $"{Prefix(store)}-{id}.state.json");

    /// <summary>The installed build's files, each with what says whether a new build changed it.</summary>
    internal static string FilesPath(GameStore store, string id) => Path.Combine(Folder, $"{Prefix(store)}-{id}.files.json");

    internal static Dictionary<string, string> ReadFiles(GameStore store, string id) =>
        ReadJson<Dictionary<string, string>>(FilesPath(store, id)) is { } files
            ? new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteJson<T>(string path, T value)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            Program.Log($"Could not keep how far a download got: {ex.Message}");
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Takes away a game the library installed, as the stores' own apps do: the files the store's build
    /// listed, the folders that leaves empty, the game's working files and its record. Anything else in the
    /// folder is the player's (saves and settings, which old games keep beside the game) and stays. The user
    /// asked for this on 2026-11-14; before, the whole folder went.
    /// </summary>
    /// <returns>Whether anything was kept in the game's folder.</returns>
    public static bool Uninstall(NativeInstall install)
    {
        // Only a folder the record names, and never a drive or a top folder.
        var kept = false;
        var folder = Path.GetFullPath(install.Folder);
        if (Path.GetPathRoot(folder) is { } root && folder.TrimEnd('\\').Length > root.Length
            && folder.Count(c => c == '\\') >= 2 && Directory.Exists(folder))
        {
            var files = ReadFiles(install.Store, install.Id);
            if (files.Count == 0)
            {
                // Missing/corrupt files.json: never recursively delete the game folder, as player saves may live there.
                Program.Log($"Skipped deleting {folder} for {install.Key}: installed file list was missing");
                kept = Directory.EnumerateFileSystemEntries(folder).Any();
            }
            else
            {
                var rootPrefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
                foreach (var path in files.Keys)
                {
                    if (!Path.IsPathRooted(path)
                        && Path.GetFullPath(Path.Combine(folder, path)) is { } full
                        && full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(full))
                    {
                        File.Delete(full);
                    }
                }

                kept = !RemoveEmptyFolders(folder);
            }
        }

        TryDelete(FilesPath(install.Store, install.Id));
        TryDelete(StatePath(install.Store, install.Id));
        Remove(install.Key);
        return kept;
    }

    /// <summary>Deletes a folder's empty folders, deepest first, and the folder itself if that empties it. True when it went.</summary>
    private static bool RemoveEmptyFolders(string folder)
    {
        var empty = true;
        foreach (var inner in Directory.GetDirectories(folder))
        {
            empty &= RemoveEmptyFolders(inner);
        }

        if (!empty || Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return false;
        }

        Directory.Delete(folder);
        return true;
    }

    /// <summary>Takes away a download that was cancelled before it finished: its files and how far it got.</summary>
    public static void Discard(GameStore store, string id, string? folder)
    {
        if (Find($"{Prefix(store)}:{id}") is null && folder is not null)
        {
            try
            {
                if (Directory.Exists(folder) && folder.Count(c => c == '\\') >= 2)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log($"Could not take away the cancelled download's files: {ex.Message}");
            }
        }

        TryDelete(StatePath(store, id));
    }

    /// <summary>The games in the record whose exe is still there, as the library lists them.</summary>
    public static IEnumerable<Game> Scan() => All().Select(AsGame).OfType<Game>();

    internal static Game? AsGame(NativeInstall install)
    {
        var exe = Path.Combine(install.Folder, install.Exe);
        if (!File.Exists(exe))
        {
            return null;
        }

        var working = install.WorkingDirectory is { } dir ? Path.Combine(install.Folder, dir) : Path.GetDirectoryName(exe);
        var icon = Path.Combine(install.Folder, $"goggame-{install.Id}.ico");
        return new Game(
            Key: install.Key,
            Title: install.Title,
            Store: install.Store,
            CoverPath: null,
            IconPath: File.Exists(icon) ? icon : exe,
            LaunchTarget: exe,
            LaunchArguments: install.Arguments,
            WorkingDirectory: working,
            InstallDirectory: install.Folder,
            ExecutablePath: exe,
            CoverUrl: install.Cover);
    }
}
