using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A Microsoft Store game for PC that the Xbox account has played, as the library keeps it.</summary>
/// <param name="Family">The game's package family name, which is how an installed one is known too.</param>
/// <param name="TitleId">Xbox Live's id for the game, which its achievements are asked for with.</param>
/// <param name="ProductId">The game's Store id, which its Store page opens with.</param>
internal sealed record XboxPlayedGame(string Family, string TitleId, string Title, string? ProductId, string? Cover);

/// <summary>
/// What the library asks Xbox Live for once signed in: the games the account has played (its title history)
/// and each game's achievements. Checked with the sign-in probe on this Ally on 2026-11-15.
///
/// Xbox Live does not say which games an account owns, nor whether it has Game Pass: the Store's collections
/// service took the library's tokens and answered with nothing, however it was asked. So Ready to install
/// shows the Store games for PC the account has played before, bought or on Game Pass alike, which the user
/// chose over showing none; a game owned and never started is not among them. Ordinary PC games the Xbox app
/// noticed being played (they have no package) are left out.
/// </summary>
internal static class XboxAccountApi
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Renewing = new(1, 1);

    private static readonly string OwnedFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "owned");

    /// <summary>The account's played games as last listed, kept between runs.</summary>
    public static string PlayedPath(string xuid) => Path.Combine(OwnedFolder, $"xbox-{xuid}.json");

    /// <summary>
    /// A new sign-in from the code Microsoft's page came back with, or null with whether Microsoft or Xbox
    /// Live said no rather than not answering.
    /// </summary>
    public static async Task<(StoreSession? Session, bool Refused)> SignInAsync(string code, CancellationToken cancel = default)
    {
        var (microsoft, refused) = await XboxAuth.ExchangeAsync(code, cancel);
        if (microsoft is null)
        {
            return (null, refused);
        }

        var (xbox, noProfile) = await XboxAuth.XboxLiveAsync(microsoft.AccessToken, cancel);
        return xbox is null
            ? (null, noProfile)
            : (new StoreSession
            {
                Store = GameStore.Xbox,
                AccountId = xbox.Xuid,
                AccountName = xbox.Gamertag,
                RefreshToken = microsoft.RefreshToken,

                // What Xbox Live's services are sent: the whole header, user hash and token together.
                AccessToken = xbox.Authorization,
                AccessExpires = xbox.Expires,
            }, false);
    }

    /// <summary>
    /// The Xbox sign-in with a token good for a while yet, renewed first if need be. Null when not signed in,
    /// when Microsoft could not be reached, or when it no longer takes the sign-in, which marks the session to
    /// sign in again.
    /// </summary>
    public static async Task<StoreSession?> FreshAsync(CancellationToken cancel = default)
    {
        if (StoreSessions.Get(GameStore.Xbox) is not { NeedsSignIn: false } session)
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
            // Renewed by whoever held the gate meanwhile. Microsoft's refresh token changes with each use,
            // so two renewals at once would end the sign-in.
            session = StoreSessions.Get(GameStore.Xbox);
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

            var (microsoft, refused) = await XboxAuth.RefreshAsync(refresh, cancel);
            if (refused)
            {
                StoreSessions.Save(session with { NeedsSignIn = true });
                Program.Log("Microsoft no longer takes the library's Xbox sign-in");
                return null;
            }

            if (microsoft is null)
            {
                return null;
            }

            // The new refresh token is kept at once: the old one is spent whatever Xbox Live says next.
            session = session with { RefreshToken = microsoft.RefreshToken };
            StoreSessions.Save(session);

            var (xbox, _) = await XboxAuth.XboxLiveAsync(microsoft.AccessToken, cancel);
            if (xbox is null)
            {
                return null;
            }

            var fresh = session with { AccessToken = xbox.Authorization, AccessExpires = xbox.Expires, AccountName = xbox.Gamertag ?? session.AccountName };
            StoreSessions.Save(fresh);
            Program.Log("Renewed the Xbox sign-in");
            return fresh;
        }
        finally
        {
            Renewing.Release();
        }
    }

    // ----- The account's picture -----

    /// <summary>Where the account's picture is kept, for the top bar.</summary>
    public static string PicturePath(string xuid) => Path.Combine(OwnedFolder, $"xbox-{xuid}.picture");

    /// <summary>
    /// The signed-in account's picture as a file, fetched from Xbox Live when there is none yet or it is over
    /// a week old. Null when not signed in or there is no picture to be had.
    /// </summary>
    public static async Task<string?> PictureFileAsync(CancellationToken cancel = default)
    {
        if (StoreSessions.Get(GameStore.Xbox) is not { NeedsSignIn: false } session)
        {
            return null;
        }

        var path = PicturePath(session.AccountId);
        var have = File.Exists(path);
        if (have && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7))
        {
            return path;
        }

        if (await FreshAsync(cancel) is not { AccessToken: { } authorization } fresh)
        {
            return have ? path : null;
        }

        var json = await XboxAuth.GetAsync("https://profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw", authorization, "3", "profile", cancel);
        if (json is null || ParsePicture(json) is not { } url)
        {
            return have ? path : null;
        }

        try
        {
            var bytes = await XboxAuth.Http.GetByteArrayAsync(url, cancel);
            Directory.CreateDirectory(OwnedFolder);
            await File.WriteAllBytesAsync(path, bytes, cancel);
            if (fresh.Picture != url && StoreSessions.Get(GameStore.Xbox) is { } now)
            {
                StoreSessions.Save(now with { Picture = url });
            }

            return path;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not fetch the Xbox account's picture: {ex.GetType().Name}");
            return have ? path : null;
        }
    }

    /// <summary>The picture's address in a profile settings answer, made secure.</summary>
    internal static string? ParsePicture(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("profileUsers", out var users)
                || users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0
                || !users[0].TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var url = settings.EnumerateArray().Where(s => Str(s, "id") == "GameDisplayPicRaw").Select(s => Str(s, "value")).FirstOrDefault(v => v is not null);
            return url is null ? null
                : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..]
                : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lists the games the account has played again and keeps them on disk.</summary>
    /// <returns>Whether the list changed, and a message for the player when Xbox Live could not be asked.</returns>
    public static async Task<(bool Changed, string? Error)> RefreshPlayedAsync(bool force, CancellationToken cancel = default)
    {
        // Asked again at most twice a day unless the player asks, as the other stores' lists are.
        if (!force && StoreSessions.Get(GameStore.Xbox) is { } saved && File.Exists(PlayedPath(saved.AccountId))
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(PlayedPath(saved.AccountId)) < TimeSpan.FromHours(12))
        {
            return (false, null);
        }

        if (await FreshAsync(cancel) is not { AccessToken: { } authorization } session)
        {
            return (false, StoreSessions.SignedIn(GameStore.Xbox) ? "Xbox could not be reached. The last list is kept."
                : StoreSessions.Get(GameStore.Xbox) is null ? null : "Xbox wants you to sign in again, under Store Accounts.");
        }

        var url = $"https://titlehub.xboxlive.com/users/xuid({Uri.EscapeDataString(session.AccountId)})/titles/titlehistory/decoration/achievement,image,productid";
        if (await XboxAuth.GetAsync(url, authorization, "2", "played games", cancel) is not { } json)
        {
            return (false, "Xbox could not be reached. The last list is kept.");
        }

        var list = ParseTitleHistory(json).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Family, StringComparer.Ordinal).ToList();
        var path = PlayedPath(session.AccountId);
        if (list.SequenceEqual(ReadPlayed(path)))
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
            Program.Log($"Could not keep the Xbox games: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// The Store games for PC in a title history: those with a package family name that run on PC. Console
    /// only games, and PC games with no package, are left out.
    /// </summary>
    internal static List<XboxPlayedGame> ParseTitleHistory(string json)
    {
        var games = new List<XboxPlayedGame>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("titles", out var titles)
                || titles.ValueKind != JsonValueKind.Array)
            {
                return games;
            }

            foreach (var title in titles.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
            {
                var pc = title.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array
                    && devices.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "PC");
                if (!pc || Str(title, "pfn") is not { } family || Str(title, "titleId") is not { } id || Str(title, "name") is not { } name)
                {
                    continue;
                }

                games.Add(new XboxPlayedGame(family, id, name, PcProductId(title), Cover(title)));
            }
        }
        catch (JsonException)
        {
        }

        return games.DistinctBy(g => g.Family, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The game's Store id for PC, or its first when none says PC.</summary>
    private static string? PcProductId(JsonElement title)
    {
        if (!title.TryGetProperty("productIdsWithDeviceTypes", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var all = products.EnumerateArray().Where(p => Str(p, "productId") is not null).ToList();
        var forPc = all.FirstOrDefault(p => p.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array
            && d.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "PC"));
        return Str(forPc.ValueKind == JsonValueKind.Object ? forPc : all.FirstOrDefault(), "productId");
    }

    /// <summary>The game's portrait art, or else its box art or the picture Xbox shows for it. The Store's addresses are made secure.</summary>
    private static string? Cover(JsonElement title)
    {
        string? Image(string type) =>
            title.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
                ? images.EnumerateArray().Where(i => Str(i, "type") == type).Select(i => Str(i, "url")).FirstOrDefault(u => u is not null)
                : null;

        var url = Image("Poster") ?? Image("BoxArt") ?? Str(title, "displayImage");
        return url is null ? null
            : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..]
            : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url
            : url;
    }

    /// <summary>The account's played games as last kept, or none.</summary>
    internal static List<XboxPlayedGame> ReadPlayed(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<XboxPlayedGame>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The signed-in account's played game with a package family name, from the list as last kept, or null.</summary>
    public static XboxPlayedGame? Find(string family) =>
        StoreSessions.Get(GameStore.Xbox) is { } session
            ? ReadPlayed(PlayedPath(session.AccountId)).FirstOrDefault(g => string.Equals(g.Family, family, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// The played games as games to install, with the key the Xbox scanner gives an installed one, and the
    /// game's Microsoft Store page as where it is installed from.
    /// </summary>
    internal static List<Game> AsGames(IEnumerable<XboxPlayedGame> played) =>
        played.Select(g => new Game(
            Key: $"xbox:{g.Family}",
            Title: g.Title,
            Store: GameStore.Xbox,
            CoverPath: null,
            IconPath: null,
            LaunchTarget: g.ProductId is { } product
                ? $"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(product)}"
                : $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(g.Family)}",
            Installed: false,
            CoverUrl: g.Cover)).ToList();

    // ----- Achievements -----

    /// <summary>
    /// A game's achievements for the signed-in account, with when each was unlocked. Null when the game has
    /// none on Xbox Live, or Xbox Live could not be asked.
    /// </summary>
    public static async Task<GameAchievements?> AchievementsAsync(StoreSession session, string gameKey, string titleId, CancellationToken cancel = default)
    {
        if (session.AccessToken is not { } authorization)
        {
            return null;
        }

        var url = $"https://achievements.xboxlive.com/users/xuid({Uri.EscapeDataString(session.AccountId)})/achievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000";
        return await XboxAuth.GetAsync(url, authorization, "2", "achievements", cancel) is { } json ? ParseAchievements(gameKey, json) : null;
    }

    /// <summary>The achievements in Xbox Live's answer. A locked one shows the description Xbox gives for it while locked.</summary>
    internal static GameAchievements? ParseAchievements(string gameKey, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("achievements", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<AchievementItem>();
            foreach (var item in items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                if (Str(item, "id") is not { } id)
                {
                    continue;
                }

                var unlocked = Str(item, "progressState") == "Achieved";

                // A locked achievement carries year 1 as its time.
                DateTimeOffset? at = unlocked && item.TryGetProperty("progression", out var progression)
                    && Str(progression, "timeUnlocked") is { } time && DateTimeOffset.TryParse(time, out var when) && when.Year > 2000
                    ? when
                    : null;
                var description = unlocked ? Str(item, "description") : Str(item, "lockedDescription") ?? Str(item, "description");
                list.Add(new AchievementItem(id, Str(item, "name") ?? id, description, unlocked, at));
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

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
