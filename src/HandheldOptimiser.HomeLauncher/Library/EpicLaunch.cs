using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Starting an Epic game the library installed, as the Epic Games Launcher starts it: the game is told which
/// account is playing and handed a short-lived code it swaps for its own sign-in, which is how it reaches
/// Epic's online services, friends and achievements. The arguments are the launcher's, as Legendary gives
/// them. A game that is started without them still runs when it needs nothing from Epic.
/// </summary>
internal static class EpicLaunch
{
    private const string ExchangeUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/exchange";

    /// <summary>Whether a game is one the library installed from Epic, which it has to start as the launcher would.</summary>
    public static bool Needs(Game game) =>
        game.Store == GameStore.Epic && game.Installed && NativeInstalls.Find(game.Key) is not null;

    /// <summary>
    /// The launcher's arguments for a game, or null when the library is not signed in to Epic or Epic could
    /// not be reached in time, when the game is started without them.
    /// </summary>
    public static async Task<string?> ArgumentsAsync(Game game, CancellationToken cancel = default)
    {
        if (NativeInstalls.Find(game.Key) is not { } install || await EpicAccountApi.FreshAsync(cancel) is not { AccessToken: { } access } session)
        {
            return null;
        }

        var code = await EpicAuth.GetAsync(ExchangeUrl, access, "launch code", cancel) is { } json ? ParseExchangeCode(json) : null;
        return code is null ? null : Arguments(code, install.Id, install.Namespace, session.AccountId, session.AccountName);
    }

    /// <summary>The code in the exchange answer.</summary>
    internal static string? ParseExchangeCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The arguments the launcher starts a game with.</summary>
    internal static string Arguments(string exchangeCode, string appName, string? ns, string accountId, string? displayName)
    {
        var arguments = new List<string>
        {
            "-AUTH_LOGIN=unused",
            $"-AUTH_PASSWORD={exchangeCode}",
            "-AUTH_TYPE=exchangecode",
            $"-epicapp={appName}",
            "-epicenv=Prod",
            "-EpicPortal",
            $"-epicuserid={accountId}",
            "-epiclocale=en",
        };

        // A name with a space or a quote in it would split the command line; the game has the account's id.
        if (displayName is { Length: > 0 } && displayName.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            arguments.Add($"-epicusername={displayName}");
        }

        if (ns is { Length: > 0 })
        {
            arguments.Add($"-epicsandboxid={ns}");
        }

        return string.Join(' ', arguments);
    }
}
