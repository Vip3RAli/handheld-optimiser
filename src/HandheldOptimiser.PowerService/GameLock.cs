using System.IO;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Finds the game for a session started from Game Bar, which has no game folder from the library. Until
/// then the busiest program counts, and once the game closes any desktop program drawing a few frames
/// would keep the session going for ever. So once one program holds a game's frame rate for
/// <see cref="HoldFor"/>, the session follows that program's folder from then on, as a library session does.
/// </summary>
internal sealed class GameLock
{
    /// <summary>The frame rate a program has to hold to be taken for the game.</summary>
    public const double GameFps = 20;

    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(10);

    private string? _path;
    private DateTime _since;

    /// <summary>Whether a program runs from inside one of the library's game folders.</summary>
    public static bool InFolders(string path, IEnumerable<string> folders) =>
        folders.Any(folder => path.StartsWith(
            // A folder without its separator would also match a sibling that only starts with the same name.
            folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>The busiest program this tick.</summary>
    /// <param name="path">Its exe, or null when nothing drew.</param>
    /// <returns>The game's folder, ending in a separator, once found; null until then.</returns>
    public string? Observe(DateTime now, string? path, double fps)
    {
        if (path is null || fps < GameFps)
        {
            _path = null;
            return null;
        }

        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _path = path;
            _since = now;
            return null;
        }

        // The separator keeps a folder from matching another that only starts with the same name.
        return now - _since >= HoldFor && Path.GetDirectoryName(path) is { } folder
            ? folder + Path.DirectorySeparatorChar
            : null;
    }
}
