using System.IO;
using System.Text;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The service's decisions that need no hardware: what a request from the library comes to, which
/// program's frames are the game's, and how a request is read off the pipe. Kept apart so they can be
/// tested on their own.
/// </summary>
internal static class AutoTdpRules
{
    /// <summary>The longest request it reads. A real one is a couple of hundred bytes.</summary>
    public const int MaxRequestBytes = 4096;

    /// <summary>
    /// What a start request comes to, held to the limits the service allows whatever the library sent:
    /// a lowest and highest limit inside the Ally's range with the highest never under the lowest, and a
    /// starting point between them.
    /// </summary>
    public static (AutoTdpSettings Settings, int StartWatts, string? GameFolder) FromRequest(AutoTdpRequest request)
    {
        var min = Math.Clamp(request.MinWatts, AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts);
        var max = Math.Clamp(request.MaxWatts, min, AutoTdpProtocol.HighestWatts);
        var settings = new AutoTdpSettings(
            Math.Clamp(request.TargetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps),
            min,
            max,
            Math.Clamp(request.TemperatureLimit, AutoTdpProtocol.LowestTemperature, AutoTdpProtocol.HighestTemperature));

        // 0 (or nonsense) starts halfway; anything else is held between the two limits.
        var start = request.StartWatts > 0 ? Math.Clamp(request.StartWatts, min, max) : (min + max + 1) / 2;
        var folder = string.IsNullOrWhiteSpace(request.GameFolder) ? null : request.GameFolder;
        return (settings, start, folder);
    }

    /// <summary>
    /// The game's frames a second: the busiest program running from its folder. Without a folder, the
    /// busiest program of all but Windows' own compositor, whose frames are every window's.
    /// </summary>
    /// <param name="pathOf">A program's full path from its process id, or null when it cannot be read.</param>
    public static double GameFps(IReadOnlyDictionary<int, double> frames, string? folder, Func<int, string?> pathOf) =>
        GameFps(frames, folder, null, pathOf).Fps;

    /// <summary>
    /// The game's frames a second, the program they came from and its process id (0 when none was found).
    /// With no folder yet, the library's game folders narrow it down, as when started from Game Bar.
    /// </summary>
    /// <param name="library">The library's game folders: only programs inside one of them count. Null when
    /// the library has not saved any, and then the busiest program counts.</param>
    public static (double Fps, string? Program, int ProcessId) GameFps(IReadOnlyDictionary<int, double> frames, string? folder,
        string[]? library, Func<int, string?> pathOf)
    {
        // With a separator on the end, C:\Games\Foo does not take in C:\Games\FooBar's programs.
        var inside = folder is null ? null : folder.TrimEnd('\\', '/') + '\\';

        var best = 0.0;
        string? program = null;
        var game = 0;
        foreach (var (processId, fps) in frames)
        {
            if (fps <= best)
            {
                continue;
            }

            var path = pathOf(processId);
            if (path is null)
            {
                continue;
            }

            var counts = inside is not null ? path.StartsWith(inside, StringComparison.OrdinalIgnoreCase)
                : library is not null ? GameLock.InFolders(path, library)
                : !string.Equals(Path.GetFileName(path), "dwm.exe", StringComparison.OrdinalIgnoreCase);

            if (counts)
            {
                best = fps;
                program = path;
                game = processId;
            }
        }

        return (best, program, game);
    }

    /// <summary>
    /// One line from the pipe, read no further than a limit so a client cannot make the service hold an
    /// endless line in memory. Null when the line runs past the limit, or nothing came.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancel, int limit = MaxRequestBytes)
    {
        var buffer = new byte[limit];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                // The client ended its side without a newline: what came is the line.
                return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r');
            }
        }

        return null;
    }

    /// <summary>Text from a client with its control characters taken out, so it cannot add lines of its own to the log.</summary>
    public static string Printable(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return builder.ToString();
    }
}
