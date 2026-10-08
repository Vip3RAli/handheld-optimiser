using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One game on sale, as IsThereAnyDeal lists it.</summary>
/// <param name="Price">The sale price, ready to show, in the currency of the player's country.</param>
/// <param name="Url">IsThereAnyDeal's link to the store page, which must be opened as given.</param>
/// <param name="Image">A wide banner for the card, or null when there is none.</param>
/// <param name="HistoricalLow">The lowest price the game has ever been at any store.</param>
internal sealed record Deal(
    string Id,
    string Title,
    string Shop,
    string Price,
    string Regular,
    int Cut,
    bool HistoricalLow,
    bool OnWishlist,
    string Url,
    string? Image,
    DateTimeOffset? Expiry);

/// <summary>
/// The Deals row below the library: games on sale, from IsThereAnyDeal with the player's own key. The
/// games on their Steam wishlist come first when they have a Steam key too, then IsThereAnyDeal's top
/// deals. The list is kept on disk and asked for again at most every few hours, only while the library
/// is in front with no game running.
/// </summary>
internal static class GameDeals
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    // Enough for a long row, without fetching banners nobody scrolls to.
    private const int MaxDeals = 24;
    private const int MaxWishlistDeals = 12;

    private const string Api = "https://api.isthereanydeal.com";
    private const int SteamShop = 61;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "deals");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    /// <summary>The player's country, from Windows' region setting, for prices in their own currency.</summary>
    public static string Country
    {
        get
        {
            try
            {
                return RegionInfo.CurrentRegion.TwoLetterISORegionName is { Length: 2 } code ? code.ToUpperInvariant() : "US";
            }
            catch (ArgumentException)
            {
                return "US";
            }
        }
    }

    /// <summary>The deals from the last answer, and when it came, without asking for anything. Empty when none is kept.</summary>
    public static (List<Deal> Deals, DateTime? Fetched) Cached()
    {
        var path = CachePath();
        try
        {
            if (File.Exists(path))
            {
                var deals = JsonSerializer.Deserialize<List<Deal>>(File.ReadAllText(path), CacheJson) ?? [];
                return (deals, File.GetLastWriteTimeUtc(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Program.Log($"The saved deals could not be read: {ex.Message}");
        }

        return ([], null);
    }

    /// <summary>
    /// Asks IsThereAnyDeal for the deals when the last answer is old, or always when <paramref name="force"/>
    /// is set. Call it off the UI thread or await it; it does nothing without a key.
    /// </summary>
    /// <returns>Whether the deals changed, and a message for the player when something went wrong.</returns>
    public static async Task<(bool Changed, string? Error)> RefreshAsync(bool force)
    {
        if (LibrarySettings.DealsKey is not { } key)
        {
            return (false, null);
        }

        var path = CachePath();
        if (!force && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RefreshAfter)
        {
            return (false, null);
        }

        var country = Country;
        try
        {
            string? wishlistError = null;
            var wishlist = new List<Deal>();
            if (LibrarySettings.WishlistDeals && LibrarySettings.SteamKey is { } steamKey)
            {
                try
                {
                    (wishlist, wishlistError) = await WishlistDealsAsync(key, steamKey, country);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    // The top deals still come without it.
                    Program.Log($"Could not match the Steam wishlist to deals: {ex.Message}");
                    wishlistError = "Your Steam wishlist could not be read. Top deals are shown.";
                }
            }

            var url = $"{Api}/deals/v2?key={Uri.EscapeDataString(key)}&country={country}&limit=60&mature=false";
            using var response = await Http.GetAsync(url);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, "IsThereAnyDeal did not accept the API key.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (false, "IsThereAnyDeal asked to wait a while. The last deals are kept.");
            }

            response.EnsureSuccessStatusCode();
            var top = ParseDeals(await response.Content.ReadAsStringAsync());

            var onWishlist = wishlist.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deals = wishlist.Concat(top.Where(d => !onWishlist.Contains(d.Id))).Take(MaxDeals).ToList();

            var json = JsonSerializer.Serialize(deals, CacheJson);
            var before = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            return (json != before, wishlistError);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            Program.Log($"Could not ask IsThereAnyDeal for deals: {ex.Message}");
            return (false, "IsThereAnyDeal could not be reached. The last deals are kept.");
        }
    }

    /// <summary>
    /// The games on the player's Steam wishlist that are on sale anywhere, biggest discount first. Steam
    /// only shares a wishlist that is public.
    /// </summary>
    private static async Task<(List<Deal> Deals, string? Error)> WishlistDealsAsync(string key, string steamKey, string country)
    {
        if (OwnedGames.SteamAccount() is not { } steamId)
        {
            return ([], "Sign in to Steam once, so your wishlist can be found.");
        }

        var wishlistUrl = $"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?key={Uri.EscapeDataString(steamKey)}&steamid={steamId}";
        var appIds = ParseWishlist(await Http.GetStringAsync(wishlistUrl));
        if (appIds.Count == 0)
        {
            return ([], "Steam shared no wishlist. In Steam's privacy settings, set Game details to Public.");
        }

        // Steam app ids to IsThereAnyDeal's game ids, then the best price on sale for each.
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in appIds.Chunk(200))
        {
            var lookup = await PostJsonAsync($"{Api}/lookup/id/shop/{SteamShop}/v1?key={Uri.EscapeDataString(key)}",
                batch.Select(id => $"app/{id}"));
            foreach (var (shopId, gameId) in ParseLookup(lookup))
            {
                if (long.TryParse(shopId.AsSpan(4), out var appId))
                {
                    ids.TryAdd(gameId, appId);
                }
            }
        }

        var onSale = new List<(string Id, DealPrice Price)>();
        foreach (var batch in ids.Keys.Chunk(200))
        {
            var prices = await PostJsonAsync(
                $"{Api}/games/prices/v3?key={Uri.EscapeDataString(key)}&country={country}&deals=true", batch);
            onSale.AddRange(ParsePrices(prices));
        }

        // Each game's title and banner, only for the ones that will be shown.
        var deals = new List<Deal>();
        foreach (var (id, price) in onSale.OrderByDescending(p => p.Price.Cut).Take(MaxWishlistDeals))
        {
            var info = await Http.GetStringAsync($"{Api}/games/info/v2?key={Uri.EscapeDataString(key)}&id={Uri.EscapeDataString(id)}");
            if (ParseInfo(info) is not { } game)
            {
                continue;
            }

            var appId = ids.GetValueOrDefault(id);
            deals.Add(new Deal(id, game.Title, price.Shop, price.Price, price.Regular, price.Cut, price.HistoricalLow,
                OnWishlist: true, price.Url,
                game.Image ?? (appId > 0 ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg" : null),
                price.Expiry));
        }

        return (deals, null);
    }

    private static async Task<string> PostJsonAsync(string url, IEnumerable<string> body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The banner for a deal's card, from the copy kept on disk or downloaded once into it. Null when it
    /// cannot be had. Call it off the UI thread.
    /// </summary>
    public static async Task<string?> BannerAsync(string url)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img";
        var path = Path.Combine(CacheDir, "banners", name);
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the banners of deals that are no longer listed.</summary>
    public static void TrimBanners(IEnumerable<Deal> kept)
    {
        try
        {
            var dir = Path.Combine(CacheDir, "banners");
            if (!Directory.Exists(dir))
            {
                return;
            }

            var keep = kept.Where(d => d.Image is not null)
                .Select(d => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.Image!)))[..24] + ".img")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => !keep.Contains(Path.GetFileName(f))))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CachePath() => Path.Combine(CacheDir, $"deals-{Country.ToLowerInvariant()}.json");

    /// <summary>The games in a /deals/v2 answer that run on Windows. Add-ons and anything with no title are left out.</summary>
    public static List<Deal> ParseDeals(string json)
    {
        var deals = new List<Deal>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return deals;
            }

            foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var type = Str(item, "type");
                if (Str(item, "id") is not { } id || Str(item, "title") is not { } title
                    || (type is not null && type != "game")
                    || !item.TryGetProperty("deal", out var deal) || ReadPrice(deal) is not { } price
                    || !RunsOnWindows(deal))
                {
                    continue;
                }

                deals.Add(new Deal(id, title, price.Shop, price.Price, price.Regular, price.Cut, price.HistoricalLow,
                    OnWishlist: false, price.Url, Banner(item), price.Expiry));
            }
        }
        catch (JsonException)
        {
        }

        return deals;
    }

    /// <summary>The Steam app ids in a GetWishlist answer, highest priority first.</summary>
    public static List<long> ParseWishlist(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return items.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("appid", out var a) && a.TryGetInt64(out _))
                .Select(i => (AppId: i.GetProperty("appid").GetInt64(),
                    Priority: i.TryGetProperty("priority", out var p) && p.TryGetInt32(out var n) ? n : int.MaxValue))
                .OrderBy(i => i.Priority)
                .Select(i => i.AppId)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The shop ids IsThereAnyDeal knows in a /lookup/id/shop answer, with its game id for each.</summary>
    public static List<(string ShopId, string GameId)> ParseLookup(string json)
    {
        var found = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return found;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } id)
                {
                    found.Add((entry.Name, id));
                }
            }
        }
        catch (JsonException)
        {
        }

        return found;
    }

    /// <summary>Price details of one deal, ready to show.</summary>
    internal sealed record DealPrice(string Shop, string Price, string Regular, int Cut, bool HistoricalLow, string Url, DateTimeOffset? Expiry);

    /// <summary>The cheapest Windows deal for each game in a /games/prices/v3 answer, for the games on sale.</summary>
    public static List<(string Id, DealPrice Price)> ParsePrices(string json)
    {
        var found = new List<(string, DealPrice)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (var game in doc.RootElement.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object))
            {
                if (Str(game, "id") is not { } id || !game.TryGetProperty("deals", out var deals) || deals.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var best = deals.EnumerateArray()
                    .Where(d => d.ValueKind == JsonValueKind.Object && RunsOnWindows(d))
                    .Select(d => (Amount: Amount(d, "price"), Price: ReadPrice(d)))
                    .Where(d => d.Price is { Cut: > 0 } && d.Amount is not null)
                    .OrderBy(d => d.Amount)
                    .Select(d => d.Price)
                    .FirstOrDefault();
                if (best is not null)
                {
                    found.Add((id, best));
                }
            }
        }
        catch (JsonException)
        {
        }

        return found;
    }

    /// <summary>A game's title and banner from a /games/info/v2 answer.</summary>
    public static (string Title, string? Image)? ParseInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "title") is { } title ? (title, Banner(doc.RootElement)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A price as the player's country writes it: £12.99, 12,99 €, $12.99.</summary>
    public static string FormatPrice(decimal amount, string currency, string country)
    {
        if (CultureFor(currency, country) is { } culture)
        {
            return amount.ToString("C", culture);
        }

        return $"{amount.ToString("0.00", CultureInfo.InvariantCulture)} {currency}";
    }

    private static readonly Dictionary<string, CultureInfo?> Cultures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A culture that writes the currency: the player's own when it does, or else one from the country.</summary>
    private static CultureInfo? CultureFor(string currency, string country)
    {
        lock (Cultures)
        {
            var cacheKey = $"{currency}-{country}";
            if (Cultures.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            static bool Writes(CultureInfo c, string currency)
            {
                try
                {
                    return new RegionInfo(c.Name).ISOCurrencySymbol.Equals(currency, StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            var current = CultureInfo.CurrentCulture;
            CultureInfo? found = null;
            if (!current.IsNeutralCulture && Writes(current, currency))
            {
                found = current;
            }
            else
            {
                var specific = CultureInfo.GetCultures(CultureTypes.SpecificCultures);
                found = specific.FirstOrDefault(c => c.Name.EndsWith("-" + country, StringComparison.OrdinalIgnoreCase)
                        && c.TwoLetterISOLanguageName == current.TwoLetterISOLanguageName && Writes(c, currency))
                    ?? specific.FirstOrDefault(c => c.Name.EndsWith("-" + country, StringComparison.OrdinalIgnoreCase) && Writes(c, currency))
                    ?? specific.FirstOrDefault(c => Writes(c, currency));
            }

            Cultures[cacheKey] = found;
            return found;
        }
    }

    private static DealPrice? ReadPrice(JsonElement deal)
    {
        if (deal.ValueKind != JsonValueKind.Object || Amount(deal, "price") is not { } price || Currency(deal, "price") is not { } currency
            || Str(deal, "url") is not { } url)
        {
            return null;
        }

        var regular = Amount(deal, "regular") ?? price;
        var cut = deal.TryGetProperty("cut", out var c) && c.TryGetInt32(out var n) ? n : 0;
        var shop = deal.TryGetProperty("shop", out var s) ? Str(s, "name") ?? "" : "";

        // At or below the lowest price ever seen, which IsThereAnyDeal also flags with "H".
        var low = Amount(deal, "historyLow");
        var historical = Str(deal, "flag") == "H" || (low is { } l && price <= l);

        DateTimeOffset? expiry = Str(deal, "expiry") is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at : null;

        var country = Country;
        return new DealPrice(shop, FormatPrice(price, currency, country), FormatPrice(regular, currency, country),
            cut, historical, url, expiry);
    }

    // Deals that list no platforms are taken to run anywhere.
    private static bool RunsOnWindows(JsonElement deal) =>
        !deal.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array
        || platforms.GetArrayLength() == 0
        || platforms.EnumerateArray().Any(p => Str(p, "name") is { } name && name.Contains("Windows", StringComparison.OrdinalIgnoreCase));

    private static decimal? Amount(JsonElement deal, string name) =>
        deal.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var amount)
            ? amount : null;

    private static string? Currency(JsonElement deal, string name) =>
        deal.TryGetProperty(name, out var p) ? Str(p, "currency") : null;

    // The widest banner that is not too big to decode for a card.
    private static string? Banner(JsonElement item) =>
        item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object
            ? Str(assets, "banner400") ?? Str(assets, "banner300") ?? Str(assets, "banner600") ?? Str(assets, "banner145")
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
