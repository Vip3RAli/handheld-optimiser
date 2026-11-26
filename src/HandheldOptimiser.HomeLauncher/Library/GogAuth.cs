using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Signing in to GOG as GOG Galaxy does, which Heroic and gogdl do too: GOG's own sign-in page, opened for
/// Galaxy's public client, hands back a code on its way to <c>embed.gog.com/on_login_success</c>, and the code
/// is swapped for an access token (an hour) and a refresh token. The refresh token does not change when it is
/// used, and it is also what a game's own token is asked for with, for its achievements. Checked with the
/// sign-in probe on this Ally on 2026-11-14.
/// </summary>
internal static class GogAuth
{
    /// <summary>GOG Galaxy's own client. Its secret ships with Galaxy, and every open GOG client uses it.</summary>
    internal const string ClientId = "46899977096215655";
    private const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";

    /// <summary>Where GOG's page goes once signed in, with the code on the end.</summary>
    internal const string Redirect = "https://embed.gog.com/on_login_success?origin=client";

    /// <summary>A client of its own that sends no cookies, so nothing of a browser's rides along with a token.</summary>
    internal static readonly HttpClient Http = HttpClients.Create(TimeSpan.FromSeconds(30), useCookies: false);

    /// <summary>GOG's sign-in page for Galaxy's client, laid out as Galaxy shows it.</summary>
    public static string SignInUrl =>
        $"https://auth.gog.com/auth?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&response_type=code&layout=client2";

    /// <summary>A sign-in answer: the tokens, when the access token runs out, and the account's Galaxy id.</summary>
    public sealed record Tokens(string AccessToken, string RefreshToken, DateTimeOffset Expires, string UserId);

    /// <summary>The code on the page GOG goes to once signed in, or null for any other page.</summary>
    public static string? CodeFrom(Uri uri)
    {
        if (!uri.Host.Equals("embed.gog.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/on_login_success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return System.Web.HttpUtility.ParseQueryString(uri.Query)["code"] is { Length: > 0 } code ? code : null;
    }

    /// <summary>The tokens in an answer from <c>auth.gog.com/token</c>, or null when it has none.</summary>
    public static Tokens? ParseTokens(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "access_token") is not { } access || Str(root, "refresh_token") is not { } refresh
                || Str(root, "user_id") is not { } user)
            {
                return null;
            }

            var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            return new Tokens(access, refresh, now.AddSeconds(seconds), user);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Swaps the code from the sign-in page for tokens.</summary>
    public static Task<(Tokens? Tokens, bool Refused)> ExchangeAsync(string code, CancellationToken cancel = default) =>
        TokenAsync($"https://auth.gog.com/token?client_id={ClientId}&client_secret={ClientSecret}&grant_type=authorization_code"
            + $"&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(Redirect)}", "sign-in", cancel);

    /// <summary>A new access token from the refresh token. Refused means GOG no longer takes the sign-in.</summary>
    public static Task<(Tokens? Tokens, bool Refused)> RefreshAsync(string refreshToken, CancellationToken cancel = default) =>
        TokenAsync($"https://auth.gog.com/token?client_id={ClientId}&client_secret={ClientSecret}&grant_type=refresh_token"
            + $"&refresh_token={Uri.EscapeDataString(refreshToken)}", "renewal", cancel);

    /// <summary>
    /// A token for a game's own client, whose id and secret are in the game's build manifest, made from the
    /// account's refresh token. GOG's achievements service only answers a game's own token.
    /// </summary>
    public static Task<(Tokens? Tokens, bool Refused)> GameTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancel = default) =>
        TokenAsync($"https://auth.gog.com/token?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}"
            + $"&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}&without_new_session=1", "game token", cancel);

    private static async Task<(Tokens?, bool)> TokenAsync(string url, string what, CancellationToken cancel)
    {
        try
        {
            using var answer = await Http.GetAsync(url, cancel);
            if (answer.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The address has secrets in it, so only what was asked for is logged.
                Program.Log($"GOG refused the {what}: {(int)answer.StatusCode}");
                return (null, true);
            }

            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"GOG answered {(int)answer.StatusCode} for the {what}");
                return (null, false);
            }

            return (ParseTokens(await answer.Content.ReadAsStringAsync(cancel), DateTimeOffset.UtcNow), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not reach GOG for the {what}: {ex.GetType().Name}");
            }

            return (null, false);
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
