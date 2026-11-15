using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Signing in to Xbox with a Microsoft account, as the open Xbox libraries do (Playnite's among them):
/// Microsoft's sign-in page, for a public client with no secret, comes back with a code; the code is swapped
/// for a Microsoft access token (an hour) and a refresh token, which changes each time it is used; the access
/// token is swapped for an Xbox Live user token, and that for a token Xbox Live's services take (16 hours),
/// sent as <c>XBL3.0 x=&lt;user hash&gt;;&lt;token&gt;</c>. In a browser allowed to use the account Windows
/// is signed in with, the page never shows: the code came straight back on this Ally. Checked with the
/// sign-in probe on 2026-11-15. Nothing here writes a token to the log.
/// </summary>
internal static class XboxAuth
{
    /// <summary>A public client for Xbox Live sign-in, which needs no secret.</summary>
    private const string ClientId = "38cd2fa8-66fd-4760-afb2-405eb65d5b0c";
    private const string Scope = "Xboxlive.signin Xboxlive.offline_access";
    private const string Redirect = "https://login.live.com/oauth20_desktop.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    /// <summary>A client of its own that sends no cookies, so nothing of a browser's rides along with a token.</summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Microsoft's sign-in page for Xbox Live, which goes to its own blank page with the code once signed in.</summary>
    public static string SignInUrl =>
        "https://login.live.com/oauth20_authorize.srf"
        + $"?client_id={ClientId}&response_type=code&approval_prompt=auto&scope={Uri.EscapeDataString(Scope)}&redirect_uri={Uri.EscapeDataString(Redirect)}";

    /// <summary>Whether a page is the one Microsoft goes to when the sign-in is over, with a code or with why not.</summary>
    public static bool IsRedirect(Uri uri) =>
        uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/oauth20_desktop.srf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The code on the page Microsoft goes to once signed in, or null for any other page or a refusal.</summary>
    public static string? CodeFrom(Uri uri) =>
        IsRedirect(uri) && System.Web.HttpUtility.ParseQueryString(uri.Query)["code"] is { Length: > 0 } code ? code : null;

    /// <summary>A Microsoft account's tokens.</summary>
    public sealed record MicrosoftTokens(string AccessToken, string RefreshToken);

    /// <summary>What Xbox Live's services take: the header to send, when it runs out, and whose it is.</summary>
    public sealed record XboxTokens(string Authorization, DateTimeOffset Expires, string Xuid, string? Gamertag);

    /// <summary>The tokens in an answer from Microsoft's token service, or null when it has none.</summary>
    public static MicrosoftTokens? ParseMicrosoftTokens(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "access_token") is { } access && Str(doc.RootElement, "refresh_token") is { } refresh
                ? new MicrosoftTokens(access, refresh)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The token in an Xbox Live user token answer.</summary>
    public static string? ParseUserToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, "Token");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The header, its end and the account in an Xbox Live service token answer.</summary>
    public static XboxTokens? ParseServiceToken(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Str(root, "Token") is not { } token || !root.TryGetProperty("DisplayClaims", out var claims)
                || !claims.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array || xui.GetArrayLength() == 0)
            {
                return null;
            }

            var user = xui[0];
            if (Str(user, "uhs") is not { } hash || Str(user, "xid") is not { } xuid)
            {
                return null;
            }

            var expires = Str(root, "NotAfter") is { } at && DateTimeOffset.TryParse(at, out var when) ? when : now.AddHours(8);
            return new XboxTokens($"XBL3.0 x={hash};{token}", expires, xuid, Str(user, "gtg"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Swaps the code from the sign-in page for the Microsoft account's tokens.</summary>
    public static Task<(MicrosoftTokens? Tokens, bool Refused)> ExchangeAsync(string code, CancellationToken cancel = default) =>
        MicrosoftAsync(new() { ["client_id"] = ClientId, ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["scope"] = Scope },
            "sign-in", cancel);

    /// <summary>New Microsoft tokens from the refresh token. Refused means Microsoft no longer takes the sign-in.</summary>
    public static Task<(MicrosoftTokens? Tokens, bool Refused)> RefreshAsync(string refreshToken, CancellationToken cancel = default) =>
        MicrosoftAsync(new() { ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["scope"] = Scope }, "renewal", cancel);

    private static async Task<(MicrosoftTokens?, bool)> MicrosoftAsync(Dictionary<string, string> form, string what, CancellationToken cancel)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var answer = await Http.PostAsync(TokenUrl, content, cancel);
            if (answer.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Program.Log($"Microsoft refused the Xbox {what}: {(int)answer.StatusCode}");
                return (null, true);
            }

            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Microsoft answered {(int)answer.StatusCode} for the Xbox {what}");
                return (null, false);
            }

            return (ParseMicrosoftTokens(await answer.Content.ReadAsStringAsync(cancel)), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not reach Microsoft for the Xbox {what}: {ex.GetType().Name}");
            }

            return (null, false);
        }
    }

    /// <summary>
    /// Xbox Live's tokens for a Microsoft access token. Refused means Xbox Live will not have the account: it
    /// has no Xbox profile, or is a child's.
    /// </summary>
    public static async Task<(XboxTokens? Tokens, bool Refused)> XboxLiveAsync(string microsoftAccessToken, CancellationToken cancel = default)
    {
        var user = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftAccessToken },
        }, "user token", cancel);
        if (user.Text is null || ParseUserToken(user.Text) is not { } userToken)
        {
            return (null, user.Refused);
        }

        var service = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            RelyingParty = "http://xboxlive.com",
            TokenType = "JWT",
            Properties = new { UserTokens = new[] { userToken }, SandboxId = "RETAIL" },
        }, "service token", cancel);
        return (service.Text is null ? null : ParseServiceToken(service.Text, DateTimeOffset.UtcNow), service.Refused);
    }

    private static async Task<(string? Text, bool Refused)> PostJsonAsync(string url, object body, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var answer = await Http.SendAsync(request, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Xbox Live answered {(int)answer.StatusCode} for the {what}");
                return (null, answer.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            }

            return (await answer.Content.ReadAsStringAsync(cancel), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not reach Xbox Live for the {what}: {ex.GetType().Name}");
            }

            return (null, false);
        }
    }

    /// <summary>Asks one of Xbox Live's services with the account's token.</summary>
    /// <returns>The answer's text, or null when Xbox Live could not be asked or said no.</returns>
    internal static async Task<string?> GetAsync(string url, string authorization, string contractVersion, string what, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var answer = await Http.SendAsync(request, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Xbox Live answered {(int)answer.StatusCode} for the {what}");
                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask Xbox Live for the {what}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
