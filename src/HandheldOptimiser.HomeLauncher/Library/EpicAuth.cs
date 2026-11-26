using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Signing in to Epic as the Epic Games Launcher does, which Legendary and Heroic do too: Epic's own sign-in
/// page, asked to come back to a page for the launcher's client, shows that page as a line of JSON with an
/// <c>authorizationCode</c> in it, and the code is swapped for an access token (about 36 hours) and a refresh
/// token (about a year). Epic gives a new refresh token each time one is used, so the newest is kept.
/// Checked with the sign-in probe on this Ally on 2026-11-14. Nothing here writes a token to the log.
/// </summary>
internal static class EpicAuth
{
    /// <summary>The Epic Games Launcher's own client. Its secret ships with the launcher, and every open Epic client uses it.</summary>
    internal const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";

    /// <summary>What the launcher calls itself, which Epic's services expect of its client.</summary>
    internal const string Agent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    private const string TokenUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";

    /// <summary>A client of its own that sends no cookies, so nothing of a browser's rides along with a token.</summary>
    internal static readonly HttpClient Http = HttpClients.Create(TimeSpan.FromSeconds(30), useCookies: false, addUserAgent: false);

    /// <summary>The page Epic shows once signed in: a line of JSON with the code in it.</summary>
    public static string RedirectUrl => $"https://www.epicgames.com/id/api/redirect?clientId={ClientId}&responseType=code";

    /// <summary>Epic's sign-in page, which goes to <see cref="RedirectUrl"/> once signed in.</summary>
    public static string SignInUrl => "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(RedirectUrl);

    /// <summary>Whether a page is the one with the code on it.</summary>
    public static bool IsRedirectPage(Uri uri) =>
        uri.Host.Equals("www.epicgames.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/id/api/redirect", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a page is one of Epic's own outside its sign-in: an account already signed in is sometimes sent
    /// to its account page or the store, and the page with the code has to be asked for from there.
    /// </summary>
    public static bool IsPastSignIn(Uri uri) =>
        (uri.Host.Equals("www.epicgames.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("store.epicgames.com", StringComparison.OrdinalIgnoreCase))
        && !uri.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The code in the redirect page's text, or null when the page has none, as when not signed in.</summary>
    public static string? ParseCode(string? pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(pageText);
            return Str(doc.RootElement, "authorizationCode");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A sign-in answer: the tokens, when the access token runs out, and whose account it is.</summary>
    public sealed record Tokens(string AccessToken, string RefreshToken, DateTimeOffset Expires, string AccountId, string? DisplayName);

    /// <summary>The tokens in an answer from Epic's token service, or null when it has none.</summary>
    public static Tokens? ParseTokens(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Str(root, "access_token") is not { } access || Str(root, "refresh_token") is not { } refresh || Str(root, "account_id") is not { } account)
            {
                return null;
            }

            var expires = Str(root, "expires_at") is { } at && DateTimeOffset.TryParse(at, out var when) ? when
                : now.AddSeconds(root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600);
            return new Tokens(access, refresh, expires, account, Str(root, "displayName"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Swaps the code from the sign-in page for tokens.</summary>
    public static Task<(Tokens? Tokens, bool Refused)> ExchangeAsync(string code, CancellationToken cancel = default) =>
        TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["token_type"] = "eg1" }, "sign-in", cancel);

    /// <summary>New tokens from the refresh token. Refused means Epic no longer takes the sign-in.</summary>
    public static Task<(Tokens? Tokens, bool Refused)> RefreshAsync(string refreshToken, CancellationToken cancel = default) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["token_type"] = "eg1" }, "renewal", cancel);

    private static async Task<(Tokens?, bool)> TokenAsync(Dictionary<string, string> form, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
            request.Headers.TryAddWithoutValidation("User-Agent", Agent);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
            using var answer = await Http.SendAsync(request, cancel);
            if (answer.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Program.Log($"Epic refused the {what}: {(int)answer.StatusCode}");
                return (null, true);
            }

            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Epic answered {(int)answer.StatusCode} for the {what}");
                return (null, false);
            }

            return (ParseTokens(await answer.Content.ReadAsStringAsync(cancel), DateTimeOffset.UtcNow), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not reach Epic for the {what}: {ex.GetType().Name}");
            }

            return (null, false);
        }
    }

    /// <summary>Asks one of Epic's services as the launcher does, with the account's access token.</summary>
    /// <returns>The answer's text, or null when Epic could not be asked or said no.</returns>
    internal static async Task<string?> GetAsync(string url, string accessToken, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", Agent);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var answer = await Http.SendAsync(request, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Epic answered {(int)answer.StatusCode} for the {what}");
                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask Epic for the {what}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
