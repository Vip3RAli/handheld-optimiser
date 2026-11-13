using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A song or playlist the widget can list and play.</summary>
/// <param name="Kind">AutoTdpProtocol.Music.Song or Playlist.</param>
/// <param name="Id">A video id for a song, a playlist id for a playlist.</param>
/// <param name="Title">Its name.</param>
/// <param name="Subtitle">The artist, album or owner, as YouTube Music writes it under the name.</param>
internal sealed record MusicItem(string Kind, string Id, string Title, string Subtitle);

/// <summary>
/// The YouTube Music player's logic that needs no browser: the addresses it plays, the requests it sends to
/// YouTube Music's own web service for playlists and search, and reading the songs and playlists out of the
/// answers. The answers are deep and change often, so they are searched for the two kinds of row YouTube
/// Music draws, wherever those sit, rather than read by a fixed path.
/// </summary>
internal static partial class YouTubeMusic
{
    public const string Origin = "https://music.youtube.com";

    /// <summary>The page the player opens on, and the one it signs in to.</summary>
    public const string Home = Origin + "/";

    /// <summary>Google's sign-in, which comes back to YouTube Music once done.</summary>
    public const string SignIn = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue="
        + "https%3A%2F%2Fmusic.youtube.com%2F";

    // Used when the page's own cannot be read. Old versions still answer.
    private const string FallbackClientVersion = "1.20241106.01.00";

    // The most items a list holds, which is more than the widget shows without scrolling.
    public const int MostItems = 25;

    /// <summary>
    /// Whether an id is one YouTube makes: letters, digits, - and _. Anything else is refused, so an id from
    /// the widget can never change the address it is put in.
    /// </summary>
    public static bool IsSafeId(string? id) => id is not null && SafeId().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex SafeId();

    /// <summary>The address that plays a song or a playlist, or null for anything not safe to play.</summary>
    public static string? PlayUrl(string? kind, string? id) => !IsSafeId(id) ? null
        : kind == AutoTdpProtocol.Music.Song ? $"{Origin}/watch?v={id}"
        : kind == AutoTdpProtocol.Music.Playlist ? $"{Origin}/watch?list={id}"
        : null;

    /// <summary>
    /// The Authorization header YouTube's web service asks of a signed-in page: SAPISIDHASH, the time and a
    /// SHA-1 of the time, the SAPISID cookie and the origin, as the page itself sends it.
    /// </summary>
    public static string AuthHeader(string sapisid, long unixSeconds)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{unixSeconds} {sapisid} {Origin}"));
        return $"SAPISIDHASH {unixSeconds}_{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// The context every request carries: the page's own when it could be read, which has the player's
    /// language and region, or a plain one naming the YouTube Music web client.
    /// </summary>
    public static JsonNode Context(string? pageContext)
    {
        if (!string.IsNullOrWhiteSpace(pageContext))
        {
            try
            {
                if (JsonNode.Parse(pageContext) is JsonObject context && context["client"] is JsonObject)
                {
                    return context;
                }
            }
            catch (JsonException)
            {
                // Read as missing.
            }
        }

        return new JsonObject
        {
            ["client"] = new JsonObject { ["clientName"] = "WEB_REMIX", ["clientVersion"] = FallbackClientVersion, ["hl"] = "en" }
        };
    }

    /// <summary>The request for the player's own playlists, in their library.</summary>
    public static string PlaylistsBody(string? pageContext) =>
        new JsonObject { ["context"] = Context(pageContext), ["browseId"] = "FEmusic_liked_playlists" }.ToJsonString();

    /// <summary>The request for a search.</summary>
    public static string SearchBody(string? pageContext, string query) =>
        new JsonObject { ["context"] = Context(pageContext), ["query"] = query }.ToJsonString();

    /// <summary>
    /// The songs and playlists in an answer, in the order shown, each once. Artists, albums without a way to
    /// play them and anything else are left out.
    /// </summary>
    public static IReadOnlyList<MusicItem> Items(string answer)
    {
        List<MusicItem> items = [];
        try
        {
            using var document = JsonDocument.Parse(answer);
            HashSet<string> seen = new(StringComparer.Ordinal);
            Walk(document.RootElement, items, seen);
        }
        catch (JsonException)
        {
            // Nothing that can be read.
        }

        return items;
    }

    private static void Walk(JsonElement element, List<MusicItem> items, HashSet<string> seen)
    {
        if (items.Count >= MostItems)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Walk(child, items, seen);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            // A row is read whole, and not searched again inside, so its menu's links make no rows of their own.
            if (property.Name is "musicResponsiveListItemRenderer" or "musicTwoRowItemRenderer")
            {
                if (Row(property.Value, property.Name == "musicTwoRowItemRenderer") is { } item && seen.Add(item.Kind + ":" + item.Id))
                {
                    items.Add(item);
                }
            }
            else
            {
                Walk(property.Value, items, seen);
            }
        }
    }

    private static MusicItem? Row(JsonElement row, bool twoRow)
    {
        var title = twoRow ? FirstRun(Child(row, "title")) : FirstRun(Column(row, 0));
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var subtitle = twoRow ? AllRuns(Child(row, "subtitle")) : AllRuns(Column(row, 1));

        if (Find(row, "watchEndpoint") is { } watch && Text(watch, "videoId") is { } videoId && IsSafeId(videoId))
        {
            return new MusicItem(AutoTdpProtocol.Music.Song, videoId, title, subtitle);
        }

        if (Find(row, "watchPlaylistEndpoint") is { } list && Text(list, "playlistId") is { } playlistId && IsSafeId(playlistId))
        {
            return new MusicItem(AutoTdpProtocol.Music.Playlist, playlistId, title, subtitle);
        }

        // A playlist's own page is VL and its id.
        if (Find(row, "browseEndpoint") is { } browse && Text(browse, "browseId") is { } browseId
            && browseId.StartsWith("VL", StringComparison.Ordinal) && IsSafeId(browseId[2..]))
        {
            return new MusicItem(AutoTdpProtocol.Music.Playlist, browseId[2..], title, subtitle);
        }

        return null;
    }

    private static JsonElement? Column(JsonElement row, int index)
    {
        if (Child(row, "flexColumns") is not { ValueKind: JsonValueKind.Array } columns || columns.GetArrayLength() <= index)
        {
            return null;
        }

        return Child(Child(columns[index], "musicResponsiveListItemFlexColumnRenderer"), "text");
    }

    private static string? FirstRun(JsonElement? text) =>
        Child(text, "runs") is { ValueKind: JsonValueKind.Array } runs && runs.GetArrayLength() > 0 ? Text(runs[0], "text") : null;

    private static string AllRuns(JsonElement? text)
    {
        if (Child(text, "runs") is not { ValueKind: JsonValueKind.Array } runs)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (var run in runs.EnumerateArray())
        {
            builder.Append(Text(run, "text"));
        }

        return builder.ToString().Trim();
    }

    /// <summary>The first object of that name anywhere inside, in the order written.</summary>
    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == name && property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value;
                }

                if (Find(property.Value, name) is { } found)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (Find(child, name) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) ? child : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
