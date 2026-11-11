using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Custom tags and collections for games in the library (such as Short Session, Docked Only, Co-op, Backlog
/// or Completed, plus any tag the player types). Stored under <c>HKCU\Software\HandheldOptimiser\HomeApp\Tags</c>
/// as semicolon-separated names per game key.
/// </summary>
internal static class GameTags
{
    private const string TagsKey = Program.SettingsKey + @"\Tags";

    public static readonly string[] BuiltInTags =
    [
        "Short Session",
        "Docked Only",
        "Co-op",
        "Backlog",
        "Completed"
    ];

    public static List<string> For(Game game)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TagsKey);
            return Parse(key?.GetValue(game.Key) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public static Dictionary<string, List<string>> All()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TagsKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                var parsed = Parse(key!.GetValue(name) as string);
                if (parsed.Count > 0)
                {
                    map[name] = parsed;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return map;
    }

    public static List<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0 && seen.Add(part))
            {
                tags.Add(part);
            }
        }

        return tags;
    }

    public static string Format(IEnumerable<string> tags)
    {
        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var trimmed = tag.Replace(';', ' ').Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                clean.Add(trimmed);
            }
        }

        return string.Join(';', clean);
    }

    public static bool Toggle(Game game, string tag)
    {
        var tags = For(game);
        var existing = tags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            tags.RemoveAt(existing);
        }
        else
        {
            tags.Add(tag.Trim());
        }

        return Save(game, tags);
    }

    public static bool Add(Game game, string tag)
    {
        var trimmed = tag.Replace(';', ' ').Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var tags = For(game);
        if (!tags.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add(trimmed);
        }

        return Save(game, tags);
    }

    public static bool Save(Game game, IEnumerable<string> tags)
    {
        var text = Format(tags);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(TagsKey);
            if (text.Length == 0)
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, text, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save tags for {game.Key}: {ex.Message}");
            return false;
        }
    }
}
