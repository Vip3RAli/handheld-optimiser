using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One step of a game's <c>goggame-&lt;id&gt;.script</c>: what GOG Galaxy does after the files are in place.</summary>
internal sealed record GogScriptAction(string Name, string Action, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// The library's part of GOG Galaxy's script interpreter. Most games marked as having a script have none; those
/// that do, such as the DOSBox ones, copy their settings from the support files into the game's folder and
/// make folders (<c>supportData</c>), and write registry values under HKLM for GOG Galaxy (<c>setRegistry</c>).
/// The library does the copying and the folders, which need no administrator. It leaves the registry values,
/// which only GOG Galaxy reads, and anything else, and says so in the log. Read from Akalabeth's script on
/// 2026-11-14.
/// </summary>
internal static class GogScript
{
    /// <summary>The install steps of a script, in order.</summary>
    public static List<GogScriptAction> Parse(string json)
    {
        var actions = new List<GogScriptAction>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return actions;
            }

            foreach (var item in list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
            {
                if (!item.TryGetProperty("install", out var install) || install.ValueKind != JsonValueKind.Object
                    || !install.TryGetProperty("action", out var action) || action.GetString() is not { } name)
                {
                    continue;
                }

                var arguments = install.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())
                    : [];
                actions.Add(new GogScriptAction(item.TryGetProperty("name", out var n) ? n.GetString() ?? name : name, name, arguments));
            }
        }
        catch (JsonException)
        {
        }

        return actions;
    }

    /// <summary>
    /// A script's path with its placeholders filled in, or null when it names a place outside the game's own
    /// folders, which the library does not touch.
    /// </summary>
    internal static string? Resolve(string path, string app, string supportDir, string productId)
    {
        var filled = path.Replace("{app}", app, StringComparison.OrdinalIgnoreCase)
            .Replace("{supportDir}", supportDir, StringComparison.OrdinalIgnoreCase)
            .Replace("{productID}", productId, StringComparison.OrdinalIgnoreCase)
            .Replace('/', '\\');
        if (filled.Contains('{'))
        {
            return null;
        }

        var full = Path.GetFullPath(filled);
        var root = Path.GetFullPath(app).TrimEnd('\\') + '\\';
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    /// <summary>Carries out the steps the library does, and returns those it left, for the log.</summary>
    /// <param name="supportDir">Where the support files were written, inside <paramref name="app"/>.</param>
    public static List<string> Run(IEnumerable<GogScriptAction> actions, string app, string supportDir, string productId)
    {
        var left = new List<string>();
        foreach (var action in actions)
        {
            if (action.Action != "supportData")
            {
                left.Add($"{action.Name} ({action.Action})");
                continue;
            }

            var target = action.Arguments.GetValueOrDefault("target") is { } t ? Resolve(t, app, supportDir, productId) : null;
            var source = action.Arguments.GetValueOrDefault("source") is { } s ? Resolve(s, app, supportDir, productId) : null;
            var overwrite = action.Arguments.GetValueOrDefault("overwrite") is not "false";
            if (target is null || (action.Arguments.ContainsKey("source") && source is null))
            {
                left.Add($"{action.Name} (outside the game's folder)");
                continue;
            }

            if (action.Arguments.GetValueOrDefault("type") == "file")
            {
                // A file that is there and not to be overwritten is left: copying over it throws, which stopped
                // an update or a check of the game's files at this step every time.
                if (source is not null && File.Exists(source) && (overwrite || !File.Exists(target)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                }

                continue;
            }

            // A folder: copied from the source with everything in it, or made empty when there is none.
            Directory.CreateDirectory(target);
            if (source is not null && Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (overwrite || !File.Exists(to))
                    {
                        File.Copy(file, to, overwrite: true);
                    }
                }
            }
        }

        return left;
    }
}
