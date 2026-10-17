using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The games the Epic Games Store gives away each week, and the ones it will give away next, for the
/// front of the Deals row. They are read from the list the store's own front page shows, which needs no
/// key but is not something Epic documents, so an answer it no longer understands lists nothing. The
/// list is kept on disk and asked for again at most every few hours, only while the Deals row is on.
/// </summary>
internal static class EpicFreeGames
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    public const string Shop = "Epic Games Store";

    private const string Feed = "https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions";

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "deals");

    private static readonly HttpClient Http = HttpClients.Shared;

    /// <summary>The free games in the last answer that are still free or still to come. Empty when none is kept.</summary>
    public static List<Deal> Cached()
    {
        var path = CachePath();
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path), DateTimeOffset.UtcNow) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"The saved Epic free games could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Asks the store for its free games when the last answer is old, or always when <paramref name="force"/>
    /// is set. Call it off the UI thread or await it.
    /// </summary>
    /// <returns>Whether the answer changed.</returns>
    public static async Task<bool> RefreshAsync(bool force)
    {
        var path = CachePath();
        try
        {
            if (!force && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RefreshAfter)
            {
                return false;
            }

            var country = GameDeals.Country;
            var json = await Http.GetStringAsync($"{Feed}?locale=en-US&country={country}&allowCountries={country}");
            var before = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            await AtomicFile.WriteAllTextAsync(path, json);
            return json != before;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not ask the Epic Games Store for its free games: {ex.Message}");
            return false;
        }
    }

    private static string CachePath() => Path.Combine(CacheDir, $"epic-free-{GameDeals.Country.ToLowerInvariant()}.json");

    /// <summary>
    /// The games in a freeGamesPromotions answer that are free at <paramref name="now"/>, soonest to end
    /// first, then the ones that will be, soonest first. The answer also lists ordinary sales and games
    /// that were free before, which are left out.
    /// </summary>
    public static List<Deal> Parse(string json, DateTimeOffset now)
    {
        var free = new List<(DateTimeOffset When, Deal Deal)>();
        var coming = new List<(DateTimeOffset When, Deal Deal)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGet(doc.RootElement, out var elements, "data", "Catalog", "searchStore", "elements")
                || elements.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var game in elements.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (Str(game, "id") is not { } id || Str(game, "title") is not { } title
                    || !game.TryGetProperty("promotions", out var promotions) || promotions.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var regular = TryGet(game, out var price, "price", "totalPrice", "fmtPrice") ? Str(price, "originalPrice") : null;
                var url = StoreUrl(game);
                var image = Image(game);

                // By their dates, whichever list they are in: an answer kept for a few hours may be from
                // before a giveaway began.
                var giveaways = Giveaways(promotions, "promotionalOffers").Concat(Giveaways(promotions, "upcomingPromotionalOffers")).ToList();
                var current = giveaways.Where(o => o.Start <= now && now < o.End)
                    .Select(o => (DateTimeOffset?)o.End).FirstOrDefault();
                var next = giveaways.Where(o => o.Start > now).OrderBy(o => o.Start)
                    .Select(o => (DateTimeOffset?)o.Start).FirstOrDefault();

                if (current is { } ends)
                {
                    free.Add((ends, new Deal($"epic-free:{id}", title, Shop, "Free", regular ?? "Free", 100, HistoricalLow: false,
                        OnWishlist: false, url, image, ends, Note: "Free this week")));
                }
                else if (next is { } starts)
                {
                    var from = starts.ToLocalTime().ToString("ddd d MMM", CultureInfo.CurrentCulture);
                    coming.Add((starts, new Deal($"epic-free:{id}", title, Shop, "Free soon", "Free soon", 0, HistoricalLow: false,
                        OnWishlist: false, url, image, Expiry: null, Note: $"Free from {from}")));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return free.OrderBy(d => d.When).Concat(coming.OrderBy(d => d.When)).Select(d => d.Deal).ToList();
    }

    /// <summary>The times a game is given away: the offers of one kind that take the whole price off.</summary>
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Giveaways(JsonElement promotions, string kind)
    {
        if (!promotions.TryGetProperty(kind, out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("promotionalOffers", out var offers)
                || offers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var offer in offers.EnumerateArray())
            {
                // The setting is the share of the price still to pay: 0 is free.
                if (TryGet(offer, out var percentage, "discountSetting", "discountPercentage")
                    && percentage.TryGetInt32(out var pay) && pay == 0
                    && Date(offer, "startDate") is { } start && Date(offer, "endDate") is { } end)
                {
                    yield return (start, end);
                }
            }
        }
    }

    /// <summary>The game's page on the store. The answer names it in one of several places.</summary>
    private static string StoreUrl(JsonElement game)
    {
        var slug = Slugs(game, "offerMappings").Concat(TryGet(game, out var ns, "catalogNs") ? Slugs(ns, "mappings") : [])
            .Append(Str(game, "productSlug"))
            .Append(Str(game, "urlSlug"))
            .FirstOrDefault(s => s is { Length: > 0 } && s != "[]");

        if (slug is null)
        {
            return "https://store.epicgames.com/free-games";
        }

        var kind = string.Equals(Str(game, "offerType"), "BUNDLE", StringComparison.OrdinalIgnoreCase) ? "bundles" : "p";
        return $"https://store.epicgames.com/{kind}/{Uri.EscapeDataString(slug.Split('/')[0])}";
    }

    private static IEnumerable<string?> Slugs(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var mappings) && mappings.ValueKind == JsonValueKind.Array
            ? mappings.EnumerateArray().Select(m => Str(m, "pageSlug")).ToList()
            : [];

    // The wide picture suits the card; the others are fallbacks.
    private static string? Image(JsonElement game)
    {
        if (!game.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var byType = images.EnumerateArray()
            .Where(i => Str(i, "type") is not null && Str(i, "url") is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => Str(i, "type")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Str(g.First(), "url")!, StringComparer.OrdinalIgnoreCase);

        return new[] { "OfferImageWide", "DieselStoreFrontWide", "featuredMedia", "Thumbnail", "OfferImageTall" }
            .Select(type => byType.GetValueOrDefault(type))
            .FirstOrDefault(url => url is not null);
    }

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Str(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;

    private static bool TryGet(JsonElement element, out JsonElement found, params string[] path)
    {
        found = element;
        foreach (var name in path)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(name, out found))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
