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
    /// Settings from a start/update request, clamped to the service's allowed limits, or null when no
    /// frame-rate target was requested (e.g. fans-only or fixed power limit).
    /// </summary>
    public static AutoTdpSettings? Settings(AutoTdpRequest request)
    {
        if (request.TargetFps <= 0)
        {
            return null;
        }

        var min = Math.Clamp(request.MinWatts > 0 ? request.MinWatts : AutoTdpProtocol.LowestWatts,
            AutoTdpProtocol.LowestWatts, AutoTdpProtocol.HighestWatts);
        var max = Math.Clamp(request.MaxWatts > 0 ? request.MaxWatts : AutoTdpProtocol.DefaultMaxWatts,
            min, AutoTdpProtocol.HighestWatts);
        return new AutoTdpSettings(
            Math.Clamp(request.TargetFps, AutoTdpProtocol.LowestFps, AutoTdpProtocol.HighestFps),
            min,
            max,
            Math.Clamp(request.TemperatureLimit > 0 ? request.TemperatureLimit : AutoTdpProtocol.DefaultTemperature,
                AutoTdpProtocol.LowestTemperature, AutoTdpProtocol.HighestTemperature));
    }

    /// <summary>Starting wattage for a controller: halfway when unset, otherwise clamped inside the settings range.</summary>
    public static int StartWatts(int requestedStartWatts, AutoTdpSettings settings) =>
        requestedStartWatts > 0
            ? Math.Clamp(requestedStartWatts, settings.MinWatts, settings.MaxWatts)
            : (settings.MinWatts + settings.MaxWatts + 1) / 2;

    /// <summary>Normalized game folder from a request, or null when blank.</summary>
    public static string? GameFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder;

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
    /// The frames a second of the program in front, for the overlay and the stats screen: the most of any
    /// process running the same program as the window's own. A browser draws a video from a process of its
    /// own, a second copy of the same program, so the window's process alone showed nothing while the
    /// widget showed the video's frames (2026-11-13). 0 when none of them presents.
    /// </summary>
    /// <param name="processId">The process that owns the window in front.</param>
    /// <param name="pathOf">A program's full path from its process id, or null when it cannot be read.</param>
    public static double ProgramFps(IReadOnlyDictionary<int, double> frames, int processId, Func<int, string?> pathOf)
    {
        var best = frames.GetValueOrDefault(processId);
        if (pathOf(processId) is not { } program)
        {
            return best;
        }

        foreach (var (other, fps) in frames)
        {
            if (fps > best && other != processId && string.Equals(pathOf(other), program, StringComparison.OrdinalIgnoreCase))
            {
                best = fps;
            }
        }

        return best;
    }

    /// <summary>Whether a program runs inside a folder explicitly excluded from Auto TDP (e.g. an Off game profile).</summary>
    public static bool IsSuppressed(string? programPath, string? suppressedFolder) =>
        !string.IsNullOrEmpty(programPath)
        && !string.IsNullOrEmpty(suppressedFolder)
        && GameLock.InFolders(programPath, [suppressedFolder]);

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

    /// <summary>The last folder of a game's path, stripped of control characters, commas and quotes for plain CSV.</summary>
    public static string CsvSafeName(string? folder) =>
        folder is null ? string.Empty : Printable(Path.GetFileName(folder.TrimEnd('\\', '/'))).Replace(',', ' ').Replace('"', ' ');

    private static readonly string[] UnsafeLoaderEnvPrefixes =
    [
        "DOTNET_",
        "CORECLR_",
        "COR_",
        "COMPlus_",
        "ASPNETCORE_",
        "MSBUILD",
        "NUGET_"
    ];

    /// <summary>
    /// Removes .NET / profiler loader variables and any user-controlled PATH from an environment block
    /// passed to a <c>TokenUIAccess</c> process, replacing PATH with <paramref name="machinePath"/> and
    /// sorting case-insensitively as Windows requires for <c>CREATE_UNICODE_ENVIRONMENT</c>.
    /// </summary>
    public static string[] SanitizeUiAccessEnvironment(IEnumerable<string> entries, string? machinePath)
    {
        var kept = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var eq = entry.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = entry.AsSpan(0, eq);
            if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blocked = false;
            foreach (var prefix in UnsafeLoaderEnvPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                kept.Add(entry);
            }
        }

        if (!string.IsNullOrWhiteSpace(machinePath))
        {
            kept.Add($"PATH={machinePath}");
        }

        kept.Sort((a, b) =>
        {
            var aEq = a.IndexOf('=');
            var bEq = b.IndexOf('=');
            var aName = aEq >= 0 ? a.AsSpan(0, aEq) : a.AsSpan();
            var bName = bEq >= 0 ? b.AsSpan(0, bEq) : b.AsSpan();
            return aName.CompareTo(bName, StringComparison.OrdinalIgnoreCase);
        });
        return [.. kept];
    }

    /// <summary>Packs environment entries into a double-null-terminated UTF-16 block for <c>CreateProcessAsUser</c>.</summary>
    public static char[] BuildEnvironmentBlock(IEnumerable<string> entries)
    {
        var chars = new List<char>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            chars.AddRange(entry);
            chars.Add('\0');
        }

        if (chars.Count == 0)
        {
            chars.Add('\0');
        }

        chars.Add('\0');
        return [.. chars];
    }
}
