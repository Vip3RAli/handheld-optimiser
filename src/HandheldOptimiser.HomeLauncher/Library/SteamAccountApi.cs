using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the library asks Steam for once signed in, with the account's own access token and no Web API key.
/// Checked with the sign-in probe on this Ally (2026-11-14): <c>GetOwnedGames</c> takes the token, the
/// <c>ISteamUserStats</c> calls do not (they want a key whatever else is sent), and the community profile's
/// achievements page as XML lists every achievement with whether and when it was unlocked. That page is open
/// to anyone for a public profile, and takes the community sign-in cookie for a private one.
/// </summary>
internal static class SteamAccountApi
{
    // Renewed this long before it runs out, so a call never goes out with a token about to lapse.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);

    // One renewal at a time: a scan and an achievements sync can both find the token old.
    private static readonly SemaphoreSlim Renewing = new(1, 1);

    private static HttpClient Http => SteamAuth.Http;

    /// <summary>
    /// The Steam sign-in with an access token good for a while yet, renewed first if need be. Null when not
    /// signed in, when Steam could not be reached, or when Steam no longer takes the sign-in, in which case
    /// the session is marked to sign in again and that is logged once.
    /// </summary>
    public static async Task<StoreSession?> FreshAsync(CancellationToken cancel = default)
    {
        if (StoreSessions.Get(GameStore.Steam) is not { NeedsSignIn: false } session)
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
            // Renewed by whoever held the gate meanwhile.
            session = StoreSessions.Get(GameStore.Steam);
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
                Program.Log("The Steam sign-in has run out and has nothing to renew it with");
                return null;
            }

            var (renewed, refused) = await SteamAuth.RenewAsync(refresh, session.AccountId, cancel);
            if (refused)
            {
                StoreSessions.Save(session with { NeedsSignIn = true });
                Program.Log("Steam no longer takes the library's sign-in");
                return null;
            }

            if (renewed is null)
            {
                return null;
            }

            var fresh = session with { AccessToken = renewed.AccessToken, AccessExpires = renewed.Expires, Cookie = renewed.Cookie };
            StoreSessions.Save(fresh);
            Program.Log("Renewed the Steam sign-in");
            return fresh;
        }
        finally
        {
            Renewing.Release();
        }
    }

    /// <summary>Steam's GetOwnedGames answer for the signed-in account, or null when it could not be had.</summary>
    public static async Task<string?> OwnedGamesJsonAsync(StoreSession session, CancellationToken cancel = default)
    {
        var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/"
            + $"?access_token={Uri.EscapeDataString(session.AccessToken ?? "")}&steamid={Uri.EscapeDataString(session.AccountId)}"
            + "&include_appinfo=1&include_played_free_games=1&format=json";
        return await GetAsync(url, null, "owned games", cancel);
    }

    /// <summary>A game's achievements for the signed-in account: the community page first, Steam's API second.</summary>
    public static async Task<GameAchievements?> AchievementsAsync(StoreSession session, string gameKey, string appId, CancellationToken cancel = default)
    {
        var page = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(session.AccountId)}/stats/{Uri.EscapeDataString(appId)}/achievements/?xml=1&l=english";
        if (await GetAsync(page, session.Cookie, "achievements page", cancel) is { } xml && ParseCommunityAchievements(gameKey, xml) is { Total: > 0 } fromPage)
        {
            return fromPage;
        }

        // The game's list, and which of them the account has, without unlock times.
        var token = Uri.EscapeDataString(session.AccessToken ?? "");
        var list = await GetAsync($"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?access_token={token}&appid={Uri.EscapeDataString(appId)}&language=english",
            null, "achievement list", cancel);
        if (list is null)
        {
            return null;
        }

        var unlocked = await GetAsync("https://api.steampowered.com/IPlayerService/GetTopAchievementsForGames/v1/"
            + $"?access_token={token}&steamid={Uri.EscapeDataString(session.AccountId)}&language=english&max_achievements=10000&appids%5B0%5D={Uri.EscapeDataString(appId)}",
            null, "unlocked achievements", cancel);
        return ParseGameAchievements(gameKey, list, unlocked);
    }

    private static async Task<string?> GetAsync(string url, string? cookie, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"steamLoginSecure={cookie}");
            }

            using var answer = await Http.SendAsync(request, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                // The address has the token in it, so only what was asked for is logged.
                Program.Log($"Steam answered {(int)answer.StatusCode} for the {what}");
                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask Steam for the {what}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    /// <summary>
    /// The achievements on a profile's community page as XML: each with its API name, name, description,
    /// <c>closed="1"</c> when unlocked and the time it was. Null for an error page (a private profile without
    /// the cookie) or a game with none.
    /// </summary>
    internal static GameAchievements? ParseCommunityAchievements(string gameKey, string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null || root.Element("error") is not null || root.Element("achievements") is not { } list)
            {
                return null;
            }

            var items = new List<AchievementItem>();
            foreach (var entry in list.Elements("achievement"))
            {
                var id = Trimmed(entry.Element("apiname"));
                if (id is null)
                {
                    continue;
                }

                var unlocked = (string?)entry.Attribute("closed") == "1";
                DateTimeOffset? at = unlocked && long.TryParse(Trimmed(entry.Element("unlockTimestamp")), NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && unix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : null;
                items.Add(new AchievementItem(id, Trimmed(entry.Element("name")) ?? id, Trimmed(entry.Element("description")), unlocked, at));
            }

            return items.Count > 0
                ? new GameAchievements(gameKey, items.Count(i => i.Unlocked), items.Count, items, DateTimeOffset.UtcNow)
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// A game's achievements from <c>GetGameAchievements</c> (the list) and <c>GetTopAchievementsForGames</c>
    /// (the ones the account has, by name, with no times). Without the second, all are shown locked.
    /// </summary>
    internal static GameAchievements? ParseGameAchievements(string gameKey, string listJson, string? unlockedJson)
    {
        try
        {
            using var list = JsonDocument.Parse(listJson);
            if (!list.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("achievements", out var all) || all.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var unlockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (unlockedJson is not null)
            {
                using var top = JsonDocument.Parse(unlockedJson);
                if (top.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
                {
                    foreach (var game in games.EnumerateArray())
                    {
                        if (game.TryGetProperty("achievements", out var got) && got.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in got.EnumerateArray())
                            {
                                if (a.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                                {
                                    unlockedNames.Add(name);
                                }
                            }
                        }
                    }
                }
            }

            var items = new List<AchievementItem>();
            foreach (var a in all.EnumerateArray())
            {
                if (!a.TryGetProperty("internal_name", out var idValue) || idValue.GetString() is not { Length: > 0 } id)
                {
                    continue;
                }

                var name = a.TryGetProperty("localized_name", out var n) && n.GetString() is { Length: > 0 } shown ? shown : id;
                var description = a.TryGetProperty("localized_desc", out var d) && d.GetString() is { Length: > 0 } text ? text : null;
                items.Add(new AchievementItem(id, name, description, unlockedNames.Contains(name)));
            }

            return items.Count > 0
                ? new GameAchievements(gameKey, items.Count(i => i.Unlocked), items.Count, items, DateTimeOffset.UtcNow)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The account's public name from its community profile, for the Store Accounts row.</summary>
    public static async Task<string?> PersonaNameAsync(string steamId, CancellationToken cancel = default) =>
        await GetAsync($"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}/?xml=1", null, "profile", cancel) is { } xml
            ? ParsePersonaName(xml)
            : null;

    internal static string? ParsePersonaName(string xml)
    {
        try
        {
            return Trimmed(XDocument.Parse(xml).Root?.Element("steamID"));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Trimmed(XElement? element) =>
        element?.Value.Trim() is { Length: > 0 } text ? WebUtility.HtmlDecode(text) : null;
}
