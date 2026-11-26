using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Signing in to Steam inside the library, and keeping the sign-in alive. Settled with the sign-in probe
/// on this Ally (2026-11-14):
/// <list type="bullet">
/// <item>The QR code: <c>IAuthenticationService/BeginAuthSessionViaQR</c> gives a link shown as a QR code,
/// which the Steam app on a phone approves; <c>PollAuthSessionStatus</c> then answers with a refresh token
/// (about seven months) and an access token (a day).</item>
/// <item>Renewing: <c>GenerateAccessTokenForApp</c> refuses a browser's refresh token (eresult 15). Steam's
/// own sign-in page turns it into cookies instead, through <c>login.steampowered.com/jwt/finalizelogin</c>
/// and a transfer to each of its sites, and the JWT in the community site's <c>steamLoginSecure</c> cookie
/// works as an access token. So the refresh token is all the library needs to keep.</item>
/// <item>The web page, the fallback: once signed in there, the <c>steamRefresh_steam</c> cookie on
/// <c>login.steampowered.com</c> holds the same kind of refresh token.</item>
/// </list>
/// Nothing here writes a token to the log.
/// </summary>
internal static class SteamAuth
{
    /// <summary>Steam's sites send their own cookies, so none are kept between requests.</summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private const string Service = "https://api.steampowered.com/IAuthenticationService/";

    /// <summary>A QR sign-in waiting to be approved.</summary>
    internal sealed record QrChallenge(string ClientId, string RequestId, string Url, TimeSpan Interval);

    internal enum PollState
    {
        /// <summary>Not approved yet.</summary>
        Waiting,

        /// <summary>Steam has replaced the code, as it does every few minutes: show the new one.</summary>
        NewCode,

        Approved,

        /// <summary>Steam has dropped the sign-in, or would not answer. Start again.</summary>
        Ended
    }

    internal sealed record PollAnswer(PollState State, string? NewUrl = null, string? NewClientId = null,
        string? RefreshToken = null, string? AccessToken = null);

    /// <summary>Starts a QR sign-in, or null when Steam could not be reached.</summary>
    public static async Task<QrChallenge?> BeginQrAsync(CancellationToken cancel)
    {
        var form = new Dictionary<string, string>
        {
            // What the Steam app shows when asked to approve, and in the account's list of devices.
            ["device_friendly_name"] = "Handheld Optimiser game library",
            // A web browser's sign-in, whose refresh token finalizelogin renews.
            ["platform_type"] = "2",
        };
        return await PostAsync("BeginAuthSessionViaQR/v1/", form, cancel) is { } json ? ParseBegin(json) : null;
    }

    /// <summary>Asks once whether the QR sign-in has been approved.</summary>
    public static async Task<PollAnswer> PollAsync(QrChallenge challenge, CancellationToken cancel)
    {
        var form = new Dictionary<string, string> { ["client_id"] = challenge.ClientId, ["request_id"] = challenge.RequestId };
        return await PostAsync("PollAuthSessionStatus/v1/", form, cancel) is { } json ? ParsePoll(json) : new PollAnswer(PollState.Ended);
    }

    internal static QrChallenge? ParseBegin(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r)
                || Text(r, "client_id") is not { } client || Text(r, "request_id") is not { } request
                || Text(r, "challenge_url") is not { } url)
            {
                return null;
            }

            var interval = r.TryGetProperty("interval", out var i) && i.TryGetDouble(out var seconds) && seconds is > 0 and < 60 ? seconds : 5;
            return new QrChallenge(client, request, url, TimeSpan.FromSeconds(interval));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static PollAnswer ParsePoll(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r))
            {
                return new PollAnswer(PollState.Ended);
            }

            if (Text(r, "refresh_token") is { } refresh)
            {
                return new PollAnswer(PollState.Approved, RefreshToken: refresh, AccessToken: Text(r, "access_token"));
            }

            if (Text(r, "new_challenge_url") is { } next)
            {
                return new PollAnswer(PollState.NewCode, next, Text(r, "new_client_id"));
            }

            return new PollAnswer(PollState.Waiting);
        }
        catch (JsonException)
        {
            return new PollAnswer(PollState.Ended);
        }
    }

    /// <summary>A fresh access token, and the community site's cookie it came in.</summary>
    internal sealed record Renewed(string AccessToken, DateTimeOffset? Expires, string Cookie);

    /// <summary>
    /// Makes a fresh access token from the refresh token, as Steam's sign-in page does. <c>Refused</c> is
    /// true when Steam answered and would not: the sign-in has been revoked or has run out.
    /// </summary>
    public static async Task<(Renewed? Result, bool Refused)> RenewAsync(string refreshToken, string steamId, CancellationToken cancel)
    {
        try
        {
            var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            using var finalize = new HttpRequestMessage(HttpMethod.Post, "https://login.steampowered.com/jwt/finalizelogin")
            {
                Content = new MultipartFormDataContent
                {
                    { new StringContent(refreshToken), "nonce" },
                    { new StringContent(session), "sessionid" },
                    { new StringContent("https://steamcommunity.com/login/home/?goto="), "redir" },
                },
            };
            finalize.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
            finalize.Headers.TryAddWithoutValidation("Referer", "https://steamcommunity.com/");
            using var answer = await Http.SendAsync(finalize, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                return (null, false);
            }

            var transfer = ParseTransfers(await answer.Content.ReadAsStringAsync(cancel))
                .FirstOrDefault(t => IsSteamCommunityUrl(t.Url));
            if (transfer.Url is null)
            {
                return (null, true);
            }

            using var settoken = new HttpRequestMessage(HttpMethod.Post, transfer.Url)
            {
                Content = new MultipartFormDataContent
                {
                    { new StringContent(transfer.Nonce), "nonce" },
                    { new StringContent(transfer.Auth), "auth" },
                    { new StringContent(steamId), "steamID" },
                },
            };
            using var set = await Http.SendAsync(settoken, cancel);
            var cookie = set.Headers.TryGetValues("Set-Cookie", out var values) ? LoginCookie(values) : null;
            if (cookie is null || SplitLoginCookie(cookie) is not { } parts)
            {
                return (null, false);
            }

            return (new Renewed(parts.Token, StoreSessions.JwtExpires(parts.Token), cookie), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Program.Log($"Could not renew the Steam sign-in: {ex.GetType().Name}");
            return (null, false);
        }
    }

    internal static bool IsSteamCommunityUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>The sites finalizelogin hands the sign-in on to, each with its one-use nonce and auth.</summary>
    internal static List<(string Url, string Nonce, string Auth)> ParseTransfers(string json)
    {
        var transfers = new List<(string, string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("transfer_info", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return transfers;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "url") is { } url && item.TryGetProperty("params", out var p)
                    && Text(p, "nonce") is { } nonce && Text(p, "auth") is { } auth)
                {
                    transfers.Add((url, nonce, auth));
                }
            }
        }
        catch (JsonException)
        {
        }

        return transfers;
    }

    /// <summary>The value of the <c>steamLoginSecure</c> cookie among a response's Set-Cookie headers.</summary>
    internal static string? LoginCookie(IEnumerable<string> setCookies) =>
        setCookies.Select(c => c.Split(';')[0].Trim())
            .Where(c => c.StartsWith("steamLoginSecure=", StringComparison.Ordinal))
            .Select(c => c["steamLoginSecure=".Length..])
            .FirstOrDefault(v => v.Length > 0 && !v.Equals("deleted", StringComparison.Ordinal));

    /// <summary>
    /// A Steam sign-in cookie's two halves: <c>steamLoginSecure</c> and <c>steamRefresh_steam</c> are both
    /// the SteamID64 and a JWT, joined by two bars, which Steam sends escaped.
    /// </summary>
    internal static (string SteamId, string Token)? SplitLoginCookie(string value)
    {
        var parts = WebUtility.UrlDecode(value).Split("||");
        return parts is [{ Length: > 0 } id, { Length: > 0 } token] && ulong.TryParse(id, out _) ? (id, token) : null;
    }

    private static async Task<string?> PostAsync(string method, Dictionary<string, string> form, CancellationToken cancel)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var answer = await Http.PostAsync(Service + method, content, cancel);
            if (!answer.IsSuccessStatusCode)
            {
                Program.Log($"Steam's sign-in service answered {(int)answer.StatusCode} to {method.Split('/')[0]}");
                return null;
            }

            // Steam says how it went in this header; 1 is OK. Anything else on a poll means the sign-in ended.
            if (answer.Headers.TryGetValues("x-eresult", out var results) && results.FirstOrDefault() is { } result && result != "1")
            {
                Program.Log($"Steam's sign-in service gave eresult {result} to {method.Split('/')[0]}");
                return null;
            }

            return await answer.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not reach Steam's sign-in service: {ex.GetType().Name}");
            }

            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
