using System.Text;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Signing in to Steam inside the library, and what is asked of Steam afterwards. The answers are laid out as
/// Steam gave them to the sign-in probe on 2026-11-14, with the ids, names and tokens made up.
/// </summary>
public class SteamSignInTests
{
    private const string SteamId = "76561190000000001";

    /// <summary>A JWT with the given claims and no real signature, as only its claims are read.</summary>
    private static string Jwt(string sub, DateTimeOffset expires)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"typ":"JWT","alg":"EdDSA"}""")}.{Part($$"""{"iss":"steam","sub":"{{sub}}","aud":["web"],"exp":{{expires.ToUnixTimeSeconds()}}}""")}.c2lnbmF0dXJl";
    }

    [Fact]
    public void A_QR_sign_in_starts_with_a_code_to_show_and_how_often_to_ask()
    {
        const string answer = """
            {"response":{"client_id":"5178033234603644","challenge_url":"https://s.team/q/1/5178033234603644","request_id":"bUF6QjFwMmlWWVBNbExoRTg=","interval":5,"allowed_confirmations":[{"confirmation_type":4}],"version":1}}
            """;

        var challenge = SteamAuth.ParseBegin(answer);

        Assert.NotNull(challenge);
        Assert.Equal("5178033234603644", challenge.ClientId);
        Assert.Equal("https://s.team/q/1/5178033234603644", challenge.Url);
        Assert.Equal(TimeSpan.FromSeconds(5), challenge.Interval);
    }

    [Fact]
    public void A_start_without_a_code_is_no_start()
    {
        Assert.Null(SteamAuth.ParseBegin("""{"response":{}}"""));
        Assert.Null(SteamAuth.ParseBegin("not json"));
    }

    [Fact]
    public void Polling_tells_waiting_a_new_code_and_approval_apart()
    {
        Assert.Equal(SteamAuth.PollState.Waiting, SteamAuth.ParsePoll("""{"response":{"had_remote_interaction":false}}""").State);

        var next = SteamAuth.ParsePoll("""{"response":{"new_challenge_url":"https://s.team/q/1/99","new_client_id":"99"}}""");
        Assert.Equal(SteamAuth.PollState.NewCode, next.State);
        Assert.Equal("https://s.team/q/1/99", next.NewUrl);
        Assert.Equal("99", next.NewClientId);

        var approved = SteamAuth.ParsePoll("""{"response":{"refresh_token":"r.e.f","access_token":"a.c.c","had_remote_interaction":true,"account_name":"someone"}}""");
        Assert.Equal(SteamAuth.PollState.Approved, approved.State);
        Assert.Equal("r.e.f", approved.RefreshToken);
        Assert.Equal("a.c.c", approved.AccessToken);

        Assert.Equal(SteamAuth.PollState.Ended, SteamAuth.ParsePoll("{}").State);
    }

    [Fact]
    public void Finalizelogin_lists_each_site_to_hand_the_sign_in_on_to()
    {
        const string answer = """
            {"steamID":"76561190000000001","redir":"/login/home/","transfer_info":[
              {"url":"https://store.steampowered.com/login/settoken","params":{"nonce":"n1","auth":"a1"}},
              {"url":"https://steamcommunity.com/login/settoken","params":{"nonce":"n2","auth":"a2"}}],
             "primary_domain":"steamcommunity.com"}
            """;

        var transfers = SteamAuth.ParseTransfers(answer);

        Assert.Equal(2, transfers.Count);
        Assert.Equal(("https://steamcommunity.com/login/settoken", "n2", "a2"), transfers[1]);
    }

    [Fact]
    public void A_refused_finalizelogin_lists_no_sites()
    {
        Assert.Empty(SteamAuth.ParseTransfers("""{"error":8}"""));
        Assert.Empty(SteamAuth.ParseTransfers("[]"));
    }

    [Fact]
    public void The_sign_in_cookie_is_found_among_the_set_cookies_and_split()
    {
        string[] headers =
        [
            "browserid=123; Path=/",
            "steamLoginSecure=deleted; expires=Thu, 01 Jan 1970 00:00:00 GMT",
            $"steamLoginSecure={SteamId}%7C%7Ceyhead.eybody.sig; Path=/; Secure; HttpOnly",
        ];

        var cookie = SteamAuth.LoginCookie(headers);

        Assert.Equal($"{SteamId}%7C%7Ceyhead.eybody.sig", cookie);
        Assert.Equal((SteamId, "eyhead.eybody.sig"), SteamAuth.SplitLoginCookie(cookie!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("eyhead.eybody.sig")]
    [InlineData("notanid||token")]
    [InlineData("76561190000000001||")]
    public void A_cookie_that_is_not_an_id_and_a_token_is_not_split(string value) =>
        Assert.Null(SteamAuth.SplitLoginCookie(value));

    [Fact]
    public void A_token_says_whose_it_is_and_when_it_runs_out()
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var token = Jwt(SteamId, expires);

        Assert.Equal(SteamId, SignInPresenter.SteamIdOf(token));
        Assert.Equal(expires, StoreSessions.JwtExpires(token));
        Assert.Null(SignInPresenter.SteamIdOf("not a token"));
        Assert.Null(StoreSessions.JwtExpires(null));
    }

    [Fact]
    public void The_community_page_gives_every_achievement_with_its_unlock_time()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?><playerstats>
              <privacyState>public</privacyState>
              <achievements>
                <achievement closed="1">
                  <iconClosed><![CDATA[https://example.invalid/a.jpg]]></iconClosed>
                  <name><![CDATA[Lilac and Gooseberries]]></name>
                  <apiname><![CDATA[lilac]]></apiname>
                  <description><![CDATA[Find Yennefer of Vengerberg.]]></description>
                  <unlockTimestamp>1651488166</unlockTimestamp>
                </achievement>
                <achievement closed="0">
                  <name><![CDATA[Card Collector]]></name>
                  <apiname><![CDATA[cards]]></apiname>
                  <description><![CDATA[]]></description>
                </achievement>
              </achievements>
            </playerstats>
            """;

        var achievements = SteamAccountApi.ParseCommunityAchievements("steam:292030", xml);

        Assert.NotNull(achievements);
        Assert.Equal(1, achievements.Unlocked);
        Assert.Equal(2, achievements.Total);
        var first = achievements.Items[0];
        Assert.Equal("lilac", first.Id);
        Assert.Equal("Lilac and Gooseberries", first.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1651488166), first.UnlockedAt);
        Assert.Null(achievements.Items[1].Description);
        Assert.Null(achievements.Items[1].UnlockedAt);
    }

    [Fact]
    public void A_private_profile_page_gives_no_achievements()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><response><error><![CDATA[This profile is private.]]></error></response>";

        Assert.Null(SteamAccountApi.ParseCommunityAchievements("steam:1", xml));
        Assert.Null(SteamAccountApi.ParseCommunityAchievements("steam:1", "<html><body>Sign in</body></html>"));
        Assert.Null(SteamAccountApi.ParseCommunityAchievements("steam:1", "not xml"));
    }

    [Fact]
    public void Without_the_page_the_list_and_the_unlocked_names_are_put_together()
    {
        const string list = """
            {"response":{"achievements":[
              {"internal_name":"LILAC","localized_name":"Lilac and Gooseberries","localized_desc":"Find Yennefer of Vengerberg.","hidden":true,"player_percent_unlocked":"60.3"},
              {"internal_name":"CARDS","localized_name":"Card Collector","localized_desc":"","hidden":false}]}}
            """;
        const string unlocked = """
            {"response":{"games":[{"appid":292030,"total_achievements":2,"achievements":[{"statid":54,"bit":9,"name":"Lilac and Gooseberries","player_percent_unlocked":"60.3"}]}]}}
            """;

        var achievements = SteamAccountApi.ParseGameAchievements("steam:292030", list, unlocked);

        Assert.NotNull(achievements);
        Assert.Equal(1, achievements.Unlocked);
        Assert.Equal(2, achievements.Total);
        Assert.True(achievements.Items.Single(i => i.Id == "LILAC").Unlocked);
        Assert.Null(achievements.Items.Single(i => i.Id == "CARDS").Description);

        // Without the unlocked answer, all are listed locked.
        Assert.Equal(0, SteamAccountApi.ParseGameAchievements("steam:292030", list, null)!.Unlocked);
    }

    [Fact]
    public void The_profile_page_gives_the_name_Steam_shows()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><profile><steamID64>76561190000000001</steamID64><steamID><![CDATA[Player One]]></steamID></profile>";

        Assert.Equal("Player One", SteamAccountApi.ParsePersonaName(xml));
        Assert.Null(SteamAccountApi.ParsePersonaName("<profile></profile>"));
    }
}
