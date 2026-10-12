using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What a store says about a game, for the panel beside the Continue playing card.</summary>
/// <param name="Released">The release date as the store writes it, such as "18 Apr, 2011".</param>
/// <param name="Source">Where the description is from when it must be credited, such as "Wikipedia".</param>
internal sealed record GameDetails(string? Description, string[] Genres, string? Released, string? Developer,
    string? Source = null);

/// <summary>
/// A game's description, genres, release date and developer from Steam's store, which needs no key.
/// Steam's own games are asked for by their app id; a game from another store is looked up by its title,
/// and only an exact match is taken. A game Steam does not sell, such as Fortnite, is looked up on
/// Wikipedia and Wikidata the same way. Each answer is kept on disk, so a game is only ever asked for
/// once. Nothing is asked for unless the player switched Store details on.
/// </summary>
internal static class StoreDetails
{
    private const string Store = "https://store.steampowered.com/api";
    private const string Wikidata = "https://www.wikidata.org/w/api.php?format=json";
    private const string Wikipedia = "https://en.wikipedia.org/api/rest_v1/page/summary/";

    // Wikidata's names for what is read from a game's entry.
    private const string GenreClaim = "P136";
    private const string DeveloperClaim = "P178";
    private const string ReleasedClaim = "P577";

    // A game Steam does not know today may be added later, but not so soon that every visit should ask.
    private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "details");

    private static readonly HttpClient Http = NewClient();

    // Wikipedia asks every program to say what it is.
    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HandheldOptimiser (https://github.com/Vip3RAli/handheld-optimiser)");
        return http;
    }

    // Going back to a game whose details are still on their way joins that request.
    private static readonly ConcurrentDictionary<string, Task<GameDetails?>> Fetches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The details kept on disk, without asking for anything. Null when none are kept.</summary>
    public static GameDetails? Cached(Game game)
    {
        var path = CachePath(game);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<GameDetails>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The game's details, asking Steam's store first if need be, or null when it does not know the game
    /// or could not be reached. Call it off the UI thread.
    /// </summary>
    public static async Task<GameDetails?> FindAsync(Game game)
    {
        if (Cached(game) is { } cached)
        {
            return cached;
        }

        // Programs the player added and ROM files are not the store's games, whatever their names match.
        if (game.IsApp || game.Store is GameStore.Other or GameStore.Emulator || RecentlyNotFound(NotFoundPath(game)))
        {
            return null;
        }

        var fetch = Fetches.GetOrAdd(game.Key, _ => FetchAsync(game));
        try
        {
            return await fetch;
        }
        finally
        {
            Fetches.TryRemove(game.Key, out _);
        }
    }

    /// <summary>Forgets what was found for the game, after the title it is looked up by changed.</summary>
    public static void Forget(Game game)
    {
        File.Delete(CachePath(game));
        File.Delete(NotFoundPath(game));
    }

    private static async Task<GameDetails?> FetchAsync(Game game)
    {
        try
        {
            var title = Artwork.SearchTitle(game) ?? game.Title;
            var appId = game.Store == GameStore.Steam && game.Key.StartsWith("steam:", StringComparison.Ordinal)
                ? game.Key[6..]
                : await SearchAsync(title);

            var details = appId is null ? null
                : ParseDetails(await Http.GetStringAsync($"{Store}/appdetails?appids={Uri.EscapeDataString(appId)}&l=english"), appId);
            details ??= await WikipediaAsync(title);

            Directory.CreateDirectory(CacheDir);
            if (details is null)
            {
                File.WriteAllBytes(NotFoundPath(game), []);
                return null;
            }

            var path = CachePath(game);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(details));
            File.Move(path + ".tmp", path, overwrite: true);
            return details;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"The store details for {game.Key} could not be fetched: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> SearchAsync(string title) =>
        ParseSearch(await Http.GetStringAsync($"{Store}/storesearch/?term={Uri.EscapeDataString(Plain(title))}&l=english&cc=US"), title);

    /// <summary>The game as Wikidata and Wikipedia describe it, or null when they have no game of that name.</summary>
    private static async Task<GameDetails?> WikipediaAsync(string title)
    {
        var search = await Http.GetStringAsync(
            $"{Wikidata}&action=wbsearchentities&type=item&language=en&limit=10&search={Uri.EscapeDataString(Plain(title))}");
        if (ParseWikidataSearch(search, title) is not { } id)
        {
            return null;
        }

        var entry = ParseWikidataEntry(
            await Http.GetStringAsync($"{Wikidata}&action=wbgetentities&props=claims|sitelinks&sitefilter=enwiki&ids={id}"), id);
        if (entry is null)
        {
            return null;
        }

        // Genres and developers are entries of their own, named by id.
        var named = entry.Genres.Take(3).Concat(entry.Developers.Take(1)).Distinct().ToList();
        var names = named.Count == 0 ? []
            : ParseWikidataLabels(await Http.GetStringAsync(
                $"{Wikidata}&action=wbgetentities&props=labels&languages=en|mul&ids={string.Join('|', named)}"));

        var description = entry.Article is null ? null
            : ParseWikipediaSummary(await Http.GetStringAsync(Wikipedia + Uri.EscapeDataString(entry.Article.Replace(' ', '_'))));

        var genres = entry.Genres.Take(3).Select(g => names.GetValueOrDefault(g)).OfType<string>().Select(Capitalised).ToArray();
        var developer = entry.Developers.Take(1).Select(d => names.GetValueOrDefault(d)).FirstOrDefault();
        return description is null && genres.Length == 0 && entry.Released is null && developer is null ? null
            : new GameDetails(description, genres, entry.Released, developer, description is null ? null : "Wikipedia");
    }

    /// <summary>What a game's Wikidata entry holds: ids for its genres and developers, and its Wikipedia article.</summary>
    internal sealed record WikidataEntry(List<string> Genres, List<string> Developers, string? Released, string? Article);

    /// <summary>
    /// The id of the game in a wbsearchentities answer whose name is the title, or null when none is.
    /// Wikidata describes a game in words such as "2017 video game developed by Epic Games".
    /// </summary>
    public static string? ParseWikidataSearch(string json, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("search", out var found)
                || found.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var wanted = Comparable(title);
            foreach (var item in found.EnumerateArray())
            {
                if (wanted.Length > 0 && Str(item, "label") is { } label && Comparable(label) == wanted
                    && Str(item, "description") is { } description && description.Contains("game", StringComparison.OrdinalIgnoreCase)
                    && Str(item, "id") is { } id)
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>The entry with the id in a wbgetentities answer, with its earliest release date ready to show.</summary>
    public static WikidataEntry? ParseWikidataEntry(string json, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entities", out var entities)
                || entities.ValueKind != JsonValueKind.Object || !entities.TryGetProperty(id, out var entity)
                || entity.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var released = Values(entity, ReleasedClaim)
                .Select(v => (Time: Str(v, "time"), Precision: v.TryGetProperty("precision", out var p) && p.TryGetInt32(out var n) ? n : 0))
                .Where(v => v.Time is { Length: >= 11 } && v.Time[0] == '+')
                .OrderBy(v => v.Time, StringComparer.Ordinal)
                .Select(v => WikidataDate(v.Time!, v.Precision))
                .FirstOrDefault(d => d is not null);

            var article = entity.TryGetProperty("sitelinks", out var links) && links.ValueKind == JsonValueKind.Object
                && links.TryGetProperty("enwiki", out var wiki) ? Str(wiki, "title") : null;

            return new WikidataEntry(
                Values(entity, GenreClaim).Select(v => Str(v, "id")).OfType<string>().ToList(),
                Values(entity, DeveloperClaim).Select(v => Str(v, "id")).OfType<string>().ToList(),
                released, article);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The English name of each entry in a wbgetentities answer, by id. A name that is the same in every
    /// language, as a company's often is, is kept under "mul" instead.
    /// </summary>
    public static Dictionary<string, string> ParseWikidataLabels(string json)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("entities", out var entities)
                && entities.ValueKind == JsonValueKind.Object)
            {
                foreach (var entity in entities.EnumerateObject())
                {
                    if (entity.Value.ValueKind == JsonValueKind.Object && entity.Value.TryGetProperty("labels", out var labels)
                        && labels.ValueKind == JsonValueKind.Object
                        && (labels.TryGetProperty("en", out var label) || labels.TryGetProperty("mul", out label))
                        && Str(label, "value") is { } name)
                    {
                        names[entity.Name] = name;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    /// <summary>The opening of the article in a Wikipedia page summary, or null when it is not an article.</summary>
    public static string? ParseWikipediaSummary(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "type") is null or "standard" ? Str(doc.RootElement, "extract")?.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What each statement of one kind says, leaving out the ones with no value.
    private static IEnumerable<JsonElement> Values(JsonElement entity, string claim)
    {
        if (!entity.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Object
            || !claims.TryGetProperty(claim, out var statements) || statements.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var statement in statements.EnumerateArray())
        {
            if (statement.ValueKind == JsonValueKind.Object && statement.TryGetProperty("mainsnak", out var snak)
                && snak.ValueKind == JsonValueKind.Object && snak.TryGetProperty("datavalue", out var data)
                && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Object)
            {
                yield return value;
            }
        }
    }

    // "+2017-07-25T00:00:00Z" as "25 Jul 2017", or only as much of it as Wikidata knows: 11 is to the day.
    private static string? WikidataDate(string time, int precision)
    {
        if (!int.TryParse(time.AsSpan(1, 4), out var year) || !int.TryParse(time.AsSpan(6, 2), out var month)
            || !int.TryParse(time.AsSpan(9, 2), out var day))
        {
            return null;
        }

        return precision >= 11 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                ? new DateTime(year, month, day).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : precision == 10 && month is >= 1 and <= 12
                ? new DateTime(year, month, 1).ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : year.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The app id of the game in a storesearch answer whose name is the title, or null when none is.</summary>
    public static string? ParseSearch(string json, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var wanted = Comparable(title);
            foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                if (wanted.Length > 0 && Str(item, "name") is { } name && Comparable(name) == wanted
                    && Str(item, "type") is null or "app"
                    && item.TryGetProperty("id", out var id) && id.TryGetInt64(out var appId))
                {
                    return appId.ToString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>The game in an appdetails answer, or null when Steam has nothing to show for the app id.</summary>
    public static GameDetails? ParseDetails(string json, string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(appId, out var app)
                || app.ValueKind != JsonValueKind.Object
                || !app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var description = Str(data, "short_description") is { } text ? WebUtility.HtmlDecode(text).Trim() : null;
            var genres = data.TryGetProperty("genres", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(g => Str(g, "description")).OfType<string>().ToArray()
                : [];
            var released = data.TryGetProperty("release_date", out var date) ? Str(date, "date") : null;
            var developer = data.TryGetProperty("developers", out var developers) && developers.ValueKind == JsonValueKind.Array
                ? developers.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString())
                    .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
                : null;

            var details = new GameDetails(string.IsNullOrEmpty(description) ? null : description, genres, released, developer);
            return details.Description is null && genres.Length == 0 && released is null && developer is null ? null : details;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Store titles carry trademark signs that Steam's search does not need.
    private static string Plain(string title) =>
        string.Concat(title.Where(c => c is not ('™' or '®' or '©'))).Trim();

    // Two names are the same game when their letters and digits are, whatever the case and punctuation.
    private static string Comparable(string title) =>
        string.Concat(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static bool RecentlyNotFound(string marker)
    {
        try
        {
            return File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < RetryNotFoundAfter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CachePath(Game game) => Path.Combine(CacheDir, FileName(game) + ".json");

    private static string NotFoundPath(Game game) => Path.Combine(CacheDir, FileName(game) + ".unknown");

    // Game keys look like "steam:620", and a colon cannot be in a file name.
    private static string FileName(Game game) =>
        string.Concat(game.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
