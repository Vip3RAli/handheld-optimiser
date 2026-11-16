using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Signing in to Xbox inside the library, and what is asked of Xbox Live afterwards. The answers are laid
/// out as Microsoft and Xbox Live gave them to the sign-in probe on 2026-11-15, with the ids, names and
/// tokens made up.
/// </summary>
public class XboxSignInTests
{
    [Fact]
    public void The_sign_in_page_is_for_Xbox_Live_and_comes_back_to_Microsofts_own_page()
    {
        var url = XboxAuth.SignInUrl;

        Assert.StartsWith("https://login.live.com/oauth20_authorize.srf?client_id=", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains(Uri.EscapeDataString("Xboxlive.signin Xboxlive.offline_access"), url);
        Assert.Contains(Uri.EscapeDataString("https://login.live.com/oauth20_desktop.srf"), url);
    }

    [Fact]
    public void The_code_is_taken_from_the_page_Microsoft_goes_to_and_a_refusal_has_none()
    {
        var done = new Uri("https://login.live.com/oauth20_desktop.srf?code=M.C123_BAY.2.U.abc&lc=1033");
        var refused = new Uri("https://login.live.com/oauth20_desktop.srf?error=access_denied&error_description=The+user+has+denied+access");

        Assert.Equal("M.C123_BAY.2.U.abc", XboxAuth.CodeFrom(done));
        Assert.True(XboxAuth.IsRedirect(refused));
        Assert.Null(XboxAuth.CodeFrom(refused));
        Assert.False(XboxAuth.IsRedirect(new Uri("https://login.live.com/oauth20_authorize.srf?client_id=x")));
        Assert.Null(XboxAuth.CodeFrom(new Uri("https://evil.example/oauth20_desktop.srf?code=abc")));
    }

    [Fact]
    public void Microsofts_tokens_are_read_and_an_answer_without_them_gives_none()
    {
        const string answer = """
            {"token_type":"bearer","expires_in":3600,"scope":"XboxLive.signin XboxLive.offline_access","access_token":"EwAccess","refresh_token":"M.Refresh","user_id":"abc"}
            """;

        Assert.Equal(new XboxAuth.MicrosoftTokens("EwAccess", "M.Refresh"), XboxAuth.ParseMicrosoftTokens(answer));
        Assert.Null(XboxAuth.ParseMicrosoftTokens("""{"error":"invalid_grant","error_description":"The refresh token has expired"}"""));
        Assert.Null(XboxAuth.ParseMicrosoftTokens("not json"));
    }

    [Fact]
    public void Xbox_Lives_tokens_become_the_header_its_services_take()
    {
        const string user = """{"IssueInstant":"2026-11-15T03:39:50Z","NotAfter":"2026-11-19T03:39:50Z","Token":"userToken","DisplayClaims":{"xui":[{"uhs":"1234567890"}]}}""";
        const string service = """
            {"IssueInstant":"2026-11-15T03:39:50Z","NotAfter":"2026-11-15T19:39:50Z","Token":"serviceToken",
             "DisplayClaims":{"xui":[{"gtg":"Player One","xid":"2533270000000001","uhs":"1234567890","agg":"Adult","prv":"184 185"}]}}
            """;

        var tokens = XboxAuth.ParseServiceToken(service, DateTimeOffset.UtcNow);

        Assert.Equal("userToken", XboxAuth.ParseUserToken(user));
        Assert.NotNull(tokens);
        Assert.Equal("XBL3.0 x=1234567890;serviceToken", tokens.Authorization);
        Assert.Equal("2533270000000001", tokens.Xuid);
        Assert.Equal("Player One", tokens.Gamertag);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 19, 39, 50, TimeSpan.Zero), tokens.Expires);

        // An account Xbox Live will not have: no profile yet.
        Assert.Null(XboxAuth.ParseServiceToken("""{"Identity":"0","XErr":2148916233,"Message":"","Redirect":"https://start.ui.xboxlive.com/CreateAccount"}""", DateTimeOffset.UtcNow));
        Assert.Null(XboxAuth.ParseUserToken("not json"));
    }

    [Fact]
    public void The_accounts_picture_is_read_from_its_profile_and_made_secure()
    {
        const string answer = """
            {"profileUsers":[{"id":"2533270000000001","hostId":"2533270000000001","settings":[
              {"id":"Gamertag","value":"Player One"},
              {"id":"GameDisplayPicRaw","value":"http://images-eds.xboxlive.com/image?url=abc&format=png"}],"isSponsoredUser":false}]}
            """;

        Assert.Equal("https://images-eds.xboxlive.com/image?url=abc&format=png", XboxAccountApi.ParsePicture(answer));
        Assert.Null(XboxAccountApi.ParsePicture("""{"profileUsers":[{"settings":[{"id":"Gamertag","value":"Player One"}]}]}"""));
        Assert.Null(XboxAccountApi.ParsePicture("""{"profileUsers":[{"settings":[{"id":"GameDisplayPicRaw","value":"file:///c:/x.png"}]}]}"""));
        Assert.Null(XboxAccountApi.ParsePicture("not json"));
    }

    private const string TitleHistory = """
        {"xuid":"2533270000000001","titles":[
          {"titleId":"1000000001","pfn":"Studio.OldDungeon_8wekyb3d8bbwe","name":"Old Dungeon","type":"Game","devices":["PC","XboxOne","XboxSeries"],
           "displayImage":"http://store-images.s-microsoft.com/image/apps.1.display",
           "productIdsWithDeviceTypes":[{"productId":"9NCONSOLE001","devices":["XboxOne"]},{"productId":"9NPCSTORE001","devices":["PC","XboxSeries"]}],
           "images":[{"url":"http://store-images.s-microsoft.com/image/apps.1.box","type":"BoxArt","caption":""},
                     {"url":"http://store-images.s-microsoft.com/image/apps.1.poster","type":"Poster","caption":""}],
           "achievement":{"currentAchievements":5,"totalAchievements":0,"currentGamerscore":50,"totalGamerscore":1000},"gamePass":{"isGamePass":true}},
          {"titleId":"1000000002","pfn":null,"name":"Space Crew","type":"Game","devices":["Win32"],"displayImage":"https://example/crew.png",
           "productIdsWithDeviceTypes":[],"images":[],"gamePass":{"isGamePass":false}},
          {"titleId":"1000000003","pfn":"Studio.ConsoleOnly_8wekyb3d8bbwe","name":"Console Only","type":"Game","devices":["XboxOne","XboxSeries"],
           "productIdsWithDeviceTypes":[{"productId":"9NCONSOLE003","devices":["XboxOne"]}],"images":[]},
          {"titleId":"1000000004","pfn":"Studio.NoArt_8wekyb3d8bbwe","name":"No Art","type":"Game","devices":["PC"],"productIdsWithDeviceTypes":[],"images":[]}
        ]}
        """;

    [Fact]
    public void The_played_games_are_the_Store_games_for_PC()
    {
        var games = XboxAccountApi.ParseTitleHistory(TitleHistory);

        Assert.Equal(["Old Dungeon", "No Art"], games.Select(g => g.Title));
        Assert.Equal(new XboxPlayedGame("Studio.OldDungeon_8wekyb3d8bbwe", "1000000001", "Old Dungeon", "9NPCSTORE001",
            "https://store-images.s-microsoft.com/image/apps.1.poster"), games[0]);
        Assert.Null(games[1].ProductId);
        Assert.Null(games[1].Cover);
        Assert.Empty(XboxAccountApi.ParseTitleHistory("not json"));
        Assert.Empty(XboxAccountApi.ParseTitleHistory("""{"titles":null}"""));
    }

    [Fact]
    public void A_played_game_is_installed_from_its_Store_page_and_shares_the_installed_games_key()
    {
        var games = XboxAccountApi.AsGames(XboxAccountApi.ParseTitleHistory(TitleHistory));

        Assert.Equal("xbox:Studio.OldDungeon_8wekyb3d8bbwe", games[0].Key);
        Assert.Equal(GameStore.Xbox, games[0].Store);
        Assert.False(games[0].Installed);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9NPCSTORE001", games[0].LaunchTarget);
        Assert.Equal("https://store-images.s-microsoft.com/image/apps.1.poster", games[0].CoverUrl);

        // With no Store id, the package's own name finds the page.
        Assert.Equal("ms-windows-store://pdp/?PFN=Studio.NoArt_8wekyb3d8bbwe", games[1].LaunchTarget);
    }

    [Fact]
    public void Achievements_are_read_with_when_they_were_unlocked()
    {
        const string answer = """
            {"achievements":[
              {"id":"1","serviceConfigId":"scid","name":"First Steps","progressState":"Achieved",
               "progression":{"requirements":[],"timeUnlocked":"2020-03-31T07:33:25.0370000Z"},
               "isSecret":false,"description":"You took a step.","lockedDescription":"Take a step.","achievementType":"Persistent",
               "rewards":[{"value":"10","type":"Gamerscore"}]},
              {"id":"2","name":"Deep Down","progressState":"NotStarted","progression":{"requirements":[],"timeUnlocked":"0001-01-01T00:00:00.0000000Z"},
               "isSecret":false,"description":"You reached the bottom.","lockedDescription":"Reach the bottom."},
              {"id":"3","name":"Halfway","progressState":"InProgress","progression":{"requirements":[],"timeUnlocked":"0001-01-01T00:00:00.0000000Z"},
               "description":"You are halfway."}
            ],"pagingInfo":{"continuationToken":null,"totalRecords":3}}
            """;

        var achievements = XboxAccountApi.ParseAchievements("xbox:Studio.OldDungeon_8wekyb3d8bbwe", answer);

        Assert.NotNull(achievements);
        Assert.Equal(3, achievements.Total);
        Assert.Equal(1, achievements.Unlocked);
        Assert.Equal(new DateTimeOffset(2020, 3, 31, 7, 33, 25, 37, TimeSpan.Zero), achievements.Items[0].UnlockedAt);
        Assert.Equal("You took a step.", achievements.Items[0].Description);
        Assert.False(achievements.Items[1].Unlocked);
        Assert.Null(achievements.Items[1].UnlockedAt);
        Assert.Equal("Reach the bottom.", achievements.Items[1].Description);
        Assert.Equal("You are halfway.", achievements.Items[2].Description);

        Assert.Null(XboxAccountApi.ParseAchievements("xbox:x", """{"achievements":[],"pagingInfo":{"totalRecords":0}}"""));
        Assert.Null(XboxAccountApi.ParseAchievements("xbox:x", "not json"));
    }
}
