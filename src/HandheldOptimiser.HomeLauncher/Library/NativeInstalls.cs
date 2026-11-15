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
    string? UpdateBuildId = null)
{
    public string Key => $"{Store.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>
/// The library's own record of the games it installed (GOG now, Epic later), in
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

    /// <summary>Takes a game out of the record.</summary>
    public static void Remove(string key) => Change(list => list.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)));

    private static void Change(Action<List<NativeInstall>> change)
    {
        lock (Gate)
        {
            var list = All();
            change(list);
            try
            {
                Directory.CreateDirectory(Folder);
                var temp = RecordPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(list));
                File.Move(temp, RecordPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log($"Could not keep the library's installs: {ex.Message}");
            }
        }
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
