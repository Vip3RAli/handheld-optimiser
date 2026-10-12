namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Which of SteamGridDB's answers to a search is the one that was asked for. For a game it is the first,
/// as SteamGridDB lists the best match first. An app needs more care: SteamGridDB is a list of games
/// with some programs among them, so a search for "Vortex" answers with four games of that name before
/// "Vortex Mod Manager (Program)", and a game's artwork on a mod manager is worse than none.
/// </summary>
internal static class ArtworkMatch
{
    // How SteamGridDB marks what is not a game: "Google Chrome (Program)", "YouTube (Website)".
    private static readonly string[] Marks = ["(Program)", "(Website)"];

    /// <summary>The answer to take, or null when none of them can be trusted to be it.</summary>
    /// <param name="results">The search's answers, in SteamGridDB's own order.</param>
    /// <param name="title">What was searched for.</param>
    /// <param name="isApp">Whether it is a program and not a game.</param>
    /// <param name="corrected">Whether the player typed the title themselves, to put a wrong match right.</param>
    public static long? Pick(IReadOnlyList<(long Id, string Name)> results, string title, bool isApp, bool corrected)
    {
        if (results.Count == 0)
        {
            return null;
        }

        if (!isApp)
        {
            return results[0].Id;
        }

        // A program or website, by SteamGridDB's own mark: the one of this name when there are several.
        var marked = results.Where(r => Unmarked(r.Name) is not null).ToList();
        if (marked.Count > 0)
        {
            var named = marked.FindIndex(r => Same(Unmarked(r.Name)!, title) || Same(r.Name, title));
            return marked[Math.Max(0, named)].Id;
        }

        // The player asked for it by this name, so the first answer is theirs to judge.
        if (corrected)
        {
            return results[0].Id;
        }

        // A program SteamGridDB has not marked, such as Visual Studio Code, is taken when it is the one
        // answer of exactly that name. Several of one name are games that happen to share it.
        var exact = results.Where(r => Same(r.Name, title)).ToList();
        return exact.Count == 1 ? exact[0].Id : null;
    }

    /// <summary>The name without its mark, or null when it has none.</summary>
    private static string? Unmarked(string name)
    {
        var trimmed = name.Trim();
        foreach (var mark in Marks)
        {
            if (trimmed.EndsWith(mark, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^mark.Length];
            }
        }

        return null;
    }

    private static bool Same(string name, string title) =>
        name.Trim().Equals(title.Trim(), StringComparison.OrdinalIgnoreCase);
}
