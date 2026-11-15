using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A GOG game the account owns, as the library keeps it: its product id, title and portrait cover.</summary>
internal sealed record GogOwnedGame(string Id, string Title, string? Cover);

/// <summary>
/// What the library asks GOG for once signed in: the account's name, the games it owns, and each game's
/// achievements. The games come from the account's own shelf (<c>getFilteredProducts</c>, games only, which
/// leaves out DLC and goodies), their portrait covers from GOG's public games database, and achievements from
/// GOG's gameplay service with a token for the game's own client. Checked with the sign-in probe on this Ally
/// on 2026-11-14. The account's email is in GOG's answers; it is never read out of them.
/// </summary>
internal static class GogAccountApi
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Renewing = new(1, 1);

    private static HttpClient Http => GogAuth.Http;

    private static readonly string OwnedFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "owned");

    /// <summary>The account's games as last listed, kept between runs.</summary>
    public static string OwnedPath(string userId) => Path.Combine(OwnedFolder, $"gog-{userId}.json");

    // Each game's own client id and secret, from its build manifest, which only changes with a new build.
    private static string ClientsPath => Path.Combine(OwnedFolder, "gog-clients.json");

    /// <summary>
    /// The GOG sign-in with an access token good for a while yet, renewed first if need be. Null when not
    /// signed in, when GOG could not be reached, or when GOG no longer takes the sign-in, which marks the
    /// session to sign in again.
    /// </summary>
    public static async Task<StoreSession?> FreshAsync(CancellationToken cancel = default)
    {
        if (StoreSessions.Get(GameStore.Gog) is not { NeedsSignIn: false } session)
        {
            return null;
        }

        if (session.AccessValid(DateTimeOffset.UtcNow, RenewBefore))
        {
            return session;
        }

        await Renewing.WaitAsync(cancel);
        try
        {
            session = StoreSessions.Get(GameStore.Gog);
            if (session is not { NeedsSignIn: false })
            {
                return null;
            }

            if (session.AccessValid(DateTimeOffset.UtcNow, RenewBefore))
            {
                return session;
            }

            if (session.RefreshToken is not { } refresh)
            {
                StoreSessions.Save(session with { NeedsSignIn = true });
                return null;
            }

            var (tokens, refused) = await GogAuth.RefreshAsync(refresh, cancel);
            if (refused)
            {
                StoreSessions.Save(session with { NeedsSignIn = true });
                Program.Log("GOG no longer takes the library's sign-in");
                return null;
            }

            if (tokens is null)
            {
                return null;
            }

            var fresh = session with { AccessToken = tokens.AccessToken, RefreshToken = tokens.RefreshToken, AccessExpires = tokens.Expires };
            StoreSessions.Save(fresh);
            return fresh;
        }
        finally
        {
            Renewing.Release();
        }
    }

    /// <summary>The name GOG shows for the account, or null when it could not be had.</summary>
    public static async Task<string?> UserNameAsync(string accessToken, CancellationToken cancel = default) =>
        await GetAsync("https://embed.gog.com/userData.json", accessToken, "account", cancel) is { } json ? ParseUserName(json) : null;

    /// <summary>The <c>username</c> in <c>userData.json</c>. Nothing else is read from it.</summary>
    internal static string? ParseUserName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "username");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ----- Owned games -----

    /// <summary>
    /// Lists the account's games again and keeps them on disk, with covers looked up for games that had none.
    /// </summary>
    /// <returns>Whether the list changed, and a message for the player when GOG could not be asked.</returns>
    public static async Task<(bool Changed, string? Error)> RefreshOwnedAsync(bool force, CancellationToken cancel = default)
    {
        // Asked again at most twice a day unless the player asks, as Steam's list is.
        if (!force && StoreSessions.Get(GameStore.Gog) is { } saved && File.Exists(OwnedPath(saved.AccountId))
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(OwnedPath(saved.AccountId)) < TimeSpan.FromHours(12))
        {
            return (false, null);
        }

        if (await FreshAsync(cancel) is not { AccessToken: { } access } session)
        {
            return (false, StoreSessions.SignedIn(GameStore.Gog) ? "GOG could not be reached. The last list is kept."
                : StoreSessions.Get(GameStore.Gog) is null ? null : "GOG wants you to sign in again, under Store Accounts.");
        }

        var products = new List<GogOwnedGame>();
        for (var page = 1; ; page++)
        {
            if (await GetAsync($"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}&sortBy=title", access, "owned games", cancel) is not { } json)
            {
                return (false, "GOG could not be reached. The last list is kept.");
            }

            var (games, pages) = ParseFilteredProducts(json);
            products.AddRange(games);
            if (page >= pages)
            {
                break;
            }
        }

        // Covers already found are kept; the rest are asked for a few at a time.
        var path = OwnedPath(session.AccountId);
        var before = ReadOwned(path);
        var known = before.Where(g => g.Cover is not null).ToDictionary(g => g.Id, g => g.Cover);
        using var gate = new SemaphoreSlim(4);
        var withCovers = await Task.WhenAll(products.Select(async game =>
        {
            if (known.TryGetValue(game.Id, out var cover))
            {
                return game with { Cover = cover };
            }

            await gate.WaitAsync(cancel);
            try
            {
                return game with { Cover = await CoverAsync(game.Id, cancel) };
            }
            finally
            {
                gate.Release();
            }
        }));

        var list = withCovers.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (list.SequenceEqual(before))
        {
            // Asked and the same: the file's time says when, for the twice a day above.
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return (false, null);
        }

        try
        {
            Directory.CreateDirectory(OwnedFolder);
            await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not keep the GOG games: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// The games on a page of <c>getFilteredProducts</c> that run on Windows, and how many pages there are.
    /// </summary>
    internal static (List<GogOwnedGame> Games, int Pages) ParseFilteredProducts(string json)
    {
        var games = new List<GogOwnedGame>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pages = root.TryGetProperty("totalPages", out var p) && p.TryGetInt32(out var n) ? n : 1;
            if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            {
                foreach (var product in products.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                {
                    var windows = product.TryGetProperty("worksOn", out var on) && on.TryGetProperty("Windows", out var w) && w.ValueKind == JsonValueKind.True;
                    var isGame = !product.TryGetProperty("isGame", out var g) || g.ValueKind != JsonValueKind.False;
                    if (!windows || !isGame || !product.TryGetProperty("id", out var id) || Str(product, "title") is not { } title)
                    {
                        continue;
                    }

                    games.Add(new GogOwnedGame(id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? string.Empty, title, null));
                }
            }

            return (games.Where(x => x.Id.Length > 0).ToList(), pages);
        }
        catch (JsonException)
        {
            return (games, 1);
        }
    }

    /// <summary>A game's portrait cover from GOG's public games database, as GOG Galaxy shows it.</summary>
    private static async Task<string?> CoverAsync(string productId, CancellationToken cancel) =>
        await GetAsync($"https://gamesdb.gog.com/platforms/gog/external_releases/{Uri.EscapeDataString(productId)}", null, "cover", cancel) is { } json
            ? ParseCover(json)
            : null;

    /// <summary>The portrait cover in a games database answer, as a JPEG address.</summary>
    internal static string? ParseCover(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("game", out var game) && game.TryGetProperty("vertical_cover", out var cover)
                && Str(cover, "url_format") is { } format
                ? format.Replace("{formatter}", "_glx_vertical_cover").Replace("{ext}", "jpg")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The account's games as last kept, or none.</summary>
    internal static List<GogOwnedGame> ReadOwned(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<GogOwnedGame>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The account's games as games to install, with the key the GOG scanners give the same game.</summary>
    internal static List<Game> AsGames(IEnumerable<GogOwnedGame> owned) =>
        owned.Select(g => new Game(
            Key: $"gog:{g.Id}",
            Title: g.Title,
            Store: GameStore.Gog,
            CoverPath: null,
            IconPath: null,
            LaunchTarget: $"goggalaxy://openGameView/{g.Id}",
            Installed: false,
            CoverUrl: g.Cover)).ToList();

    // ----- Achievements -----

    private sealed record GameClient(string ClientId, string ClientSecret);

    private static readonly Lock ClientsGate = new();

    /// <summary>
    /// A game's achievements for the signed-in account. Null when the game has none on GOG, or GOG could not
    /// be asked. The game's own client comes from its newest build's manifest, kept once found.
    /// </summary>
    public static async Task<GameAchievements?> AchievementsAsync(StoreSession session, string gameKey, string productId, CancellationToken cancel = default)
    {
        if (session.RefreshToken is not { } refresh || await ClientOfAsync(productId, cancel) is not { } client)
        {
            return null;
        }

        var (token, refused) = await GogAuth.GameTokenAsync(client.ClientId, client.ClientSecret, refresh, cancel);
        if (token is null)
        {
            // GOG refusing the account's refresh token means the sign-in is over: a password change ends every
            // session (seen 2026-11-14). The row says Sign in again at once, rather than when the hour is up.
            if (refused && await GogAuth.RefreshAsync(refresh, cancel) is (null, true) && StoreSessions.Get(GameStore.Gog) is { } current)
            {
                StoreSessions.Save(current with { NeedsSignIn = true });
                Program.Log("GOG no longer takes the library's sign-in");
            }

            return null;
        }

        var url = $"https://gameplay.gog.com/clients/{Uri.EscapeDataString(client.ClientId)}/users/{Uri.EscapeDataString(session.AccountId)}/achievements";
        return await GetAsync(url, token.AccessToken, "achievements", cancel) is { } json ? ParseAchievements(gameKey, json) : null;
    }

    /// <summary>The achievements in a gameplay service answer, with when each was unlocked.</summary>
    internal static GameAchievements? ParseAchievements(string gameKey, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<AchievementItem>();
            foreach (var item in items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                if ((Str(item, "achievement_key") ?? Str(item, "achievement_id")) is not { } id)
                {
                    continue;
                }

                DateTimeOffset? at = Str(item, "date_unlocked") is { } date && DateTimeOffset.TryParse(date, out var when) ? when : null;
                list.Add(new AchievementItem(id, Str(item, "name") ?? id, Str(item, "description"), at is not null, at));
            }

            return list.Count > 0
                ? new GameAchievements(gameKey, list.Count(i => i.Unlocked), list.Count, list, DateTimeOffset.UtcNow)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<GameClient?> ClientOfAsync(string productId, CancellationToken cancel)
    {
        var clients = ReadClients();
        if (clients.TryGetValue(productId, out var known))
        {
            return known;
        }

        if (await GogContent.LatestBuildAsync(productId, cancel) is not { } build
            || await GogContent.ManifestAsync(build, cancel) is not { ClientId: { } id, ClientSecret: { } secret })
        {
            return null;
        }

        var client = new GameClient(id, secret);
        lock (ClientsGate)
        {
            clients = ReadClients();
            clients[productId] = client;
            try
            {
                Directory.CreateDirectory(OwnedFolder);
                File.WriteAllText(ClientsPath, JsonSerializer.Serialize(clients));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return client;
    }

    private static Dictionary<string, GameClient> ReadClients()
    {
        try
        {
            return File.Exists(ClientsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, GameClient>>(File.ReadAllText(ClientsPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    // ----- Asking -----

    private static async Task<string?> GetAsync(string url, string? bearer, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            using var answer = await Http.SendAsync(request, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                // A game missing from GOG's games database has no cover there, which is no news.
                if (!(what == "cover" && answer.StatusCode == System.Net.HttpStatusCode.NotFound))
                {
                    Program.Log($"GOG answered {(int)answer.StatusCode} for the {what}");
                }

                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask GOG for the {what}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
