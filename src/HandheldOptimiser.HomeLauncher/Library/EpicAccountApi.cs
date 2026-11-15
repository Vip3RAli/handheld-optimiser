using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One thing in the Epic account's library, as Epic's library service lists it.</summary>
internal sealed record EpicLibraryRecord(string AppName, string Namespace, string CatalogItemId);

/// <summary>
/// Something the Epic account owns, as the library keeps it once its catalogue entry has been read. Entries
/// that are not games (add-ons, hidden content, engine assets) are kept too, so they are not asked about again.
/// </summary>
internal sealed record EpicOwnedGame(string AppName, string Namespace, string CatalogItemId, string Title, string? Cover, bool IsGame);

/// <summary>
/// What the library asks Epic for once signed in: the account's library (Epic's library service, a few
/// pages) and each entry's title, cover and kind (Epic's catalogue service, one question per entry, asked
/// once and kept). Checked with the sign-in probe on this Ally on 2026-11-14, when the account had 481
/// entries. Epic has no achievements service the library can ask yet. A game in the list is installed by
/// the library itself (<see cref="EpicInstaller"/>); its launcher address is kept for installing it with
/// the Epic Games Launcher instead.
/// </summary>
internal static class EpicAccountApi
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Renewing = new(1, 1);

    private const string LibraryUrl = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true";
    private const string CatalogUrl = "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/";

    private static readonly string OwnedFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "owned");

    /// <summary>The account's library as last listed, kept between runs.</summary>
    public static string OwnedPath(string accountId) => Path.Combine(OwnedFolder, $"epic-{accountId}.json");

    /// <summary>
    /// The Epic sign-in with an access token good for a while yet, renewed first if need be. Null when not
    /// signed in, when Epic could not be reached, or when Epic no longer takes the sign-in, which marks the
    /// session to sign in again.
    /// </summary>
    public static async Task<StoreSession?> FreshAsync(CancellationToken cancel = default)
    {
        if (StoreSessions.Get(GameStore.Epic) is not { NeedsSignIn: false } session)
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
            // Renewed by whoever held the gate meanwhile. Epic's refresh token is good once, so two
            // renewals at once would end the sign-in.
            session = StoreSessions.Get(GameStore.Epic);
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

            var (tokens, refused) = await EpicAuth.RefreshAsync(refresh, cancel);
            if (refused)
            {
                StoreSessions.Save(session with { NeedsSignIn = true });
                Program.Log("Epic no longer takes the library's sign-in");
                return null;
            }

            if (tokens is null)
            {
                return null;
            }

            var fresh = session with
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccessExpires = tokens.Expires,
                AccountName = tokens.DisplayName ?? session.AccountName,
            };
            StoreSessions.Save(fresh);
            Program.Log("Renewed the Epic sign-in");
            return fresh;
        }
        finally
        {
            Renewing.Release();
        }
    }

    /// <summary>
    /// Lists the account's library again and keeps it on disk, asking the catalogue only about entries not
    /// seen before.
    /// </summary>
    /// <returns>Whether the games changed, and a message for the player when Epic could not be asked.</returns>
    public static async Task<(bool Changed, string? Error)> RefreshOwnedAsync(bool force, CancellationToken cancel = default)
    {
        // Asked again at most twice a day unless the player asks, as Steam's and GOG's lists are.
        if (!force && StoreSessions.Get(GameStore.Epic) is { } saved && File.Exists(OwnedPath(saved.AccountId))
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(OwnedPath(saved.AccountId)) < TimeSpan.FromHours(12))
        {
            return (false, null);
        }

        if (await FreshAsync(cancel) is not { AccessToken: { } access } session)
        {
            return (false, StoreSessions.SignedIn(GameStore.Epic) ? "Epic could not be reached. The last list is kept."
                : StoreSessions.Get(GameStore.Epic) is null ? null : "Epic wants you to sign in again, under Store Accounts.");
        }

        var records = new List<EpicLibraryRecord>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var url = cursor is null ? LibraryUrl : $"{LibraryUrl}&cursor={Uri.EscapeDataString(cursor)}";
            if (await EpicAuth.GetAsync(url, access, "library", cancel) is not { } json)
            {
                return (false, "Epic could not be reached. The last list is kept.");
            }

            var (found, next) = ParseLibrary(json);
            records.AddRange(found);
            cursor = next;
            if (cursor is null)
            {
                break;
            }
        }

        // What the catalogue said about an entry before is kept; the rest are asked about a few at a time. An
        // entry the catalogue would not answer for is left out this time and asked about again the next.
        var path = OwnedPath(session.AccountId);
        var before = ReadOwned(path);
        var known = before.ToDictionary(g => g.CatalogItemId, g => g);
        using var gate = new SemaphoreSlim(6);
        var asked = 0;
        var entries = await Task.WhenAll(records.DistinctBy(r => r.CatalogItemId).Select(async record =>
        {
            if (known.TryGetValue(record.CatalogItemId, out var kept))
            {
                return kept;
            }

            await gate.WaitAsync(cancel);
            try
            {
                Interlocked.Increment(ref asked);
                var url = $"{CatalogUrl}{Uri.EscapeDataString(record.Namespace)}/bulk/items?id={Uri.EscapeDataString(record.CatalogItemId)}"
                    + "&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en-US";
                return await EpicAuth.GetAsync(url, access, "catalogue", cancel) is { } json ? ParseCatalogItem(json, record) : null;
            }
            finally
            {
                gate.Release();
            }
        }));

        var list = entries.OfType<EpicOwnedGame>().OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.AppName, StringComparer.Ordinal).ToList();
        if (asked > 0)
        {
            Program.Log($"Epic library: {records.Count} entries, {asked} new to the catalogue, {list.Count(g => g.IsGame)} games");
        }

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
            Program.Log($"Could not keep the Epic games: {ex.Message}");
        }

        return (!list.Where(g => g.IsGame).SequenceEqual(before.Where(g => g.IsGame)), null);
    }

    /// <summary>
    /// The entries on a page of the library service that could be Windows games, and the cursor for the next
    /// page, or null on the last. Unreal Engine's own assets (the <c>ue</c> namespace) are left out.
    /// </summary>
    internal static (List<EpicLibraryRecord> Records, string? NextCursor) ParseLibrary(string json)
    {
        var records = new List<EpicLibraryRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in list.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
                {
                    // An entry that lists no platforms is taken to run anywhere.
                    var windows = !record.TryGetProperty("platform", out var platforms) || platforms.ValueKind != JsonValueKind.Array
                        || platforms.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "Windows");
                    if (windows && Str(record, "appName") is { } app && Str(record, "namespace") is { } ns && ns != "ue"
                        && Str(record, "catalogItemId") is { } item && Str(record, "sandboxType") is null or "PUBLIC")
                    {
                        records.Add(new EpicLibraryRecord(app, ns, item));
                    }
                }
            }

            var cursor = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("responseMetadata", out var meta) ? Str(meta, "nextCursor") : null;
            return (records, cursor);
        }
        catch (JsonException)
        {
            return (records, null);
        }
    }

    /// <summary>
    /// What the catalogue says about a library entry: its title, its portrait cover, and whether it is a game
    /// of its own. An add-on names the game it belongs to, and content that only comes with another game is
    /// filed as hidden. Null when the answer does not have the entry.
    /// </summary>
    internal static EpicOwnedGame? ParseCatalogItem(string json, EpicLibraryRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(record.CatalogItemId, out var item)
                || item.ValueKind != JsonValueKind.Object || Str(item, "title") is not { } title)
            {
                return null;
            }

            var categories = item.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(x => Str(x, "path")).OfType<string>().ToList()
                : [];
            var addOn = (item.TryGetProperty("mainGameItem", out var main) && Str(main, "id") is not null) || categories.Contains("addons");
            var isGame = categories.Contains("games") && !addOn;
            var cover = KeyImage(item, "DieselGameBoxTall") ?? KeyImage(item, "OfferImageTall");
            return new EpicOwnedGame(record.AppName, record.Namespace, record.CatalogItemId, title, cover, isGame);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The account's library as last kept, or nothing.</summary>
    internal static List<EpicOwnedGame> ReadOwned(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<EpicOwnedGame>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The signed-in account's game with an app name, from the list as last kept, or null.</summary>
    public static EpicOwnedGame? Find(string appName) =>
        StoreSessions.Get(GameStore.Epic) is { } session
            ? ReadOwned(OwnedPath(session.AccountId)).FirstOrDefault(g => g.IsGame && g.AppName == appName)
            : null;

    /// <summary>
    /// The account's games as games to install, with the key the Epic scanners give the same game and the
    /// launcher's own install address, as the launcher's catalogue gave them before.
    /// </summary>
    internal static List<Game> AsGames(IEnumerable<EpicOwnedGame> owned) =>
        owned.Where(g => g.IsGame).Select(g => new Game(
            Key: $"epic:{g.AppName}",
            Title: g.Title,
            Store: GameStore.Epic,
            CoverPath: null,
            IconPath: null,
            LaunchTarget: $"com.epicgames.launcher://apps/{Uri.EscapeDataString($"{g.Namespace}:{g.CatalogItemId}:{g.AppName}")}?action=install",
            Installed: false,
            CoverUrl: g.Cover)).ToList();

    private static string? KeyImage(JsonElement item, string type) =>
        item.TryGetProperty("keyImages", out var images) && images.ValueKind == JsonValueKind.Array
            ? images.EnumerateArray().Where(i => Str(i, "type") == type).Select(i => Str(i, "url")).FirstOrDefault(u => u is not null)
            : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
