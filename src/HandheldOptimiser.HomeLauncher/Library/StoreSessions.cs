using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// A store account signed in inside the library: who it is, and the tokens that let the library ask the
/// store for its games and achievements. The tokens are as good as the password, so a session is only ever
/// held in memory or on disk encrypted for the Windows user, and never written to the log.
/// </summary>
internal sealed record StoreSession
{
    public required GameStore Store { get; init; }

    /// <summary>The store's id for the account (a SteamID64 for Steam).</summary>
    public required string AccountId { get; init; }

    /// <summary>The name to show, or null when the store gave none.</summary>
    public string? AccountName { get; init; }

    /// <summary>The long-lived token the short-lived ones are made from.</summary>
    public string? RefreshToken { get; init; }

    public string? AccessToken { get; init; }

    public DateTimeOffset? AccessExpires { get; init; }

    /// <summary>A store's own web cookie, for pages that only answer a signed-in browser.</summary>
    public string? Cookie { get; init; }

    /// <summary>Where the account's picture is, for a store that has one (Xbox).</summary>
    public string? Picture { get; init; }

    /// <summary>The store no longer accepts the tokens: the player has to sign in again.</summary>
    public bool NeedsSignIn { get; init; }

    /// <summary>Whether the access token is good for at least <paramref name="margin"/> more.</summary>
    public bool AccessValid(DateTimeOffset now, TimeSpan margin) =>
        AccessToken is { Length: > 0 } && AccessExpires is { } expires && expires - now > margin;

    /// <summary>What the Store Accounts row says about this account.</summary>
    public string Describe() => NeedsSignIn ? "Sign in again"
        : AccountName is { Length: > 0 } name ? $"Signed in as {name}"
        : "Signed in";
}

/// <summary>
/// The library's own store sign-ins, one file per store in <c>%LOCALAPPDATA%\HandheldOptimiser\accounts</c>,
/// encrypted with DPAPI for the current Windows user as the API keys are. Signing out deletes the file.
/// </summary>
internal static class StoreSessions
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lock Gate = new();

    // Read once and kept: the menus ask on the window's thread, and every scan asks again.
    private static readonly Dictionary<GameStore, StoreSession?> Loaded = [];

    internal static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "accounts");

    /// <summary>The folder the sessions are kept in. Tests point it somewhere of their own.</summary>
    internal static string Folder { get; set; } = DefaultFolder;

    /// <summary>The store's session, or null when the library is not signed in to it.</summary>
    public static StoreSession? Get(GameStore store)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(store, out var session))
            {
                session = Read(store);
                Loaded[store] = session;
            }

            return session;
        }
    }

    /// <summary>Whether the library is signed in to the store and the store still takes the sign-in.</summary>
    public static bool SignedIn(GameStore store) => Get(store) is { NeedsSignIn: false };

    /// <summary>Saves a session, encrypted. Returns false when Windows would not encrypt it or the disk refused.</summary>
    public static bool Save(StoreSession session)
    {
        var bytes = LibrarySettings.Protect(JsonSerializer.Serialize(session, Json));
        if (bytes is null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(session.Store);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not save the {Game.NameOf(session.Store)} sign-in: {ex.Message}");
            return false;
        }

        lock (Gate)
        {
            Loaded[session.Store] = session;
        }

        return true;
    }

    /// <summary>Forgets the store's sign-in.</summary>
    public static void Delete(GameStore store)
    {
        try
        {
            File.Delete(PathOf(store));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not delete the {Game.NameOf(store)} sign-in: {ex.Message}");
        }

        lock (Gate)
        {
            Loaded[store] = null;
        }
    }

    /// <summary>Drops what was read for a store, so the next ask reads its file again. For tests.</summary>
    internal static void Forget(GameStore store)
    {
        lock (Gate)
        {
            Loaded.Remove(store);
        }
    }

    private static StoreSession? Read(GameStore store)
    {
        var path = PathOf(store);
        try
        {
            if (!File.Exists(path) || LibrarySettings.Unprotect(File.ReadAllBytes(path)) is not { } text)
            {
                return null;
            }

            return JsonSerializer.Deserialize<StoreSession>(text, Json) is { Store: var saved } session && saved == store ? session : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file from another Windows account, or a broken one: as good as signed out.
            Program.Log($"Could not read the {Game.NameOf(store)} sign-in: {ex.GetType().Name}");
            return null;
        }
    }

    private static string PathOf(GameStore store) => Path.Combine(Folder, $"{store.ToString().ToLowerInvariant()}.bin");

    /// <summary>The claims of a JWT, read without checking its signature: only to know when it runs out and whose it is.</summary>
    internal static JsonElement? JwtClaims(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>When a JWT runs out, or null when it says nothing about that.</summary>
    internal static DateTimeOffset? JwtExpires(string? jwt) =>
        JwtClaims(jwt) is { } claims && claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
