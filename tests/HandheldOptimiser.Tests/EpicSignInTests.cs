using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Signing in to Epic inside the library, and what is asked of Epic afterwards. The answers are laid out as
/// Epic gave them to the sign-in probe on 2026-11-14, with the ids, names and tokens made up.
/// </summary>
public class EpicSignInTests
{
    [Fact]
    public void The_sign_in_page_comes_back_to_the_launchers_code_page()
    {
        Assert.StartsWith("https://www.epicgames.com/id/login?redirectUrl=", EpicAuth.SignInUrl);
        Assert.Contains(Uri.EscapeDataString(EpicAuth.RedirectUrl), EpicAuth.SignInUrl);
        Assert.Contains("clientId=34a02cf8f4414e29b15921876da36f9a", EpicAuth.RedirectUrl);
        Assert.Contains("responseType=code", EpicAuth.RedirectUrl);
    }

    [Fact]
    public void The_code_page_and_Epics_pages_past_the_sign_in_are_told_apart()
    {
        Assert.True(EpicAuth.IsRedirectPage(new Uri(EpicAuth.RedirectUrl)));
        Assert.False(EpicAuth.IsRedirectPage(new Uri("https://www.epicgames.com/id/login")));
        Assert.False(EpicAuth.IsRedirectPage(new Uri("https://evil.example/id/api/redirect?clientId=x")));

        Assert.True(EpicAuth.IsPastSignIn(new Uri("https://www.epicgames.com/account/personal")));
        Assert.True(EpicAuth.IsPastSignIn(new Uri("https://store.epicgames.com/en-US/")));
        Assert.False(EpicAuth.IsPastSignIn(new Uri("https://www.epicgames.com/id/login/epic")));
        Assert.False(EpicAuth.IsPastSignIn(new Uri(EpicAuth.RedirectUrl)));
        Assert.False(EpicAuth.IsPastSignIn(new Uri("https://accounts.google.com/signin")));
    }

    [Fact]
    public void The_code_is_read_from_the_page_and_a_page_without_one_gives_none()
    {
        Assert.Equal("c0de", EpicAuth.ParseCode("""{"redirectUrl":"https://localhost/launcher/authorized?code=c0de","authorizationCode":"c0de","exchangeCode":null,"sid":null}"""));
        Assert.Null(EpicAuth.ParseCode("""{"redirectUrl":"https://www.epicgames.com/id/login","authorizationCode":null,"sid":null}"""));
        Assert.Null(EpicAuth.ParseCode("Sign in to continue"));
        Assert.Null(EpicAuth.ParseCode(null));
    }

    [Fact]
    public void Tokens_are_read_with_when_they_run_out_and_whose_they_are()
    {
        const string answer = """
            {"access_token":"eg1~access","expires_in":129600,"expires_at":"2026-11-16T02:37:00.000Z","token_type":"bearer",
             "refresh_token":"eg1~refresh","refresh_expires":31536000,"refresh_expires_at":"2027-11-14T14:37:00.000Z",
             "account_id":"0123456789abcdef0123456789abcdef","client_id":"34a02cf8f4414e29b15921876da36f9a","displayName":"Player1","app":"launcher"}
            """;

        var tokens = EpicAuth.ParseTokens(answer, DateTimeOffset.UtcNow);

        Assert.NotNull(tokens);
        Assert.Equal("eg1~access", tokens.AccessToken);
        Assert.Equal("eg1~refresh", tokens.RefreshToken);
        Assert.Equal("0123456789abcdef0123456789abcdef", tokens.AccountId);
        Assert.Equal("Player1", tokens.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 2, 37, 0, TimeSpan.Zero), tokens.Expires);
    }

    [Fact]
    public void An_answer_without_tokens_gives_none_and_a_missing_time_uses_the_seconds()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(EpicAuth.ParseTokens("""{"errorCode":"errors.com.epicgames.account.auth_token.invalid_refresh_token"}""", now));
        Assert.Null(EpicAuth.ParseTokens("not json", now));
        Assert.Equal(now.AddSeconds(600),
            EpicAuth.ParseTokens("""{"access_token":"a","refresh_token":"r","account_id":"1","expires_in":600}""", now)!.Expires);
    }

    [Fact]
    public void The_library_lists_Windows_entries_and_gives_the_next_pages_cursor()
    {
        const string page = """
            {"responseMetadata":{"nextCursor":"next1","stateToken":"state"},"records":[
              {"acquisitionDate":"2018-03-12T10:32:15.171Z","appName":"Lupin","catalogItemId":"item1","namespace":"ns1","platform":["Windows","Mac"],
               "productId":"prod1","recordType":"APPLICATION","sandboxName":"Old Dungeon","sandboxType":"PUBLIC"},
              {"appName":"MacOnly","catalogItemId":"item2","namespace":"ns2","platform":["Mac"],"recordType":"APPLICATION","sandboxType":"PUBLIC"},
              {"appName":"EngineAsset","catalogItemId":"item3","namespace":"ue","platform":["Windows"],"recordType":"APPLICATION","sandboxType":"PUBLIC"},
              {"catalogItemId":"item4","namespace":"ns4","recordType":"APPLICATION","sandboxType":"PUBLIC"},
              {"appName":"NoPlatforms","catalogItemId":"item5","namespace":"ns5","recordType":"APPLICATION"},
              {"appName":"Private","catalogItemId":"item6","namespace":"ns6","platform":["Windows"],"sandboxType":"PRIVATE"}
            ]}
            """;

        var (records, cursor) = EpicAccountApi.ParseLibrary(page);

        Assert.Equal([new EpicLibraryRecord("Lupin", "ns1", "item1"), new EpicLibraryRecord("NoPlatforms", "ns5", "item5")], records);
        Assert.Equal("next1", cursor);

        Assert.Null(EpicAccountApi.ParseLibrary("""{"responseMetadata":{"stateToken":"state"},"records":[]}""").NextCursor);
        Assert.Empty(EpicAccountApi.ParseLibrary("not json").Records);
    }

    [Fact]
    public void The_catalogue_gives_a_games_title_and_portrait_cover()
    {
        const string answer = """
            {"item1":{"id":"item1","title":"Old Dungeon","namespace":"ns1","categories":[{"path":"games"},{"path":"applications"}],
              "keyImages":[{"type":"DieselGameBox","url":"https://cdn.example/wide.jpg","width":2560,"height":1440},
                           {"type":"DieselGameBoxTall","url":"https://cdn.example/tall.jpg","width":1200,"height":1600}],
              "mainGameItemList":[],"releaseInfo":[{"id":"r1","appId":"Lupin","platform":["Windows"]}]}}
            """;

        var game = EpicAccountApi.ParseCatalogItem(answer, new EpicLibraryRecord("Lupin", "ns1", "item1"));

        Assert.Equal(new EpicOwnedGame("Lupin", "ns1", "item1", "Old Dungeon", "https://cdn.example/tall.jpg", IsGame: true), game);
    }

    [Fact]
    public void Add_ons_and_hidden_content_are_kept_but_are_not_games()
    {
        const string addOn = """
            {"dlc1":{"id":"dlc1","title":"Old Dungeon: The Cellar","categories":[{"path":"addons"},{"path":"games"}],
              "mainGameItem":{"id":"item1","namespace":"ns1"},"keyImages":[{"type":"OfferImageTall","url":"https://cdn.example/dlc.jpg"}]}}
            """;
        const string hidden = """{"h1":{"id":"h1","title":"Brick Content","categories":[{"path":"hidden"}],"mainGameItem":{"id":"item9"},"keyImages":[]}}""";

        var dlc = EpicAccountApi.ParseCatalogItem(addOn, new EpicLibraryRecord("Cellar", "ns1", "dlc1"));
        var content = EpicAccountApi.ParseCatalogItem(hidden, new EpicLibraryRecord("Bricks", "ns9", "h1"));

        Assert.False(dlc!.IsGame);
        Assert.Equal("https://cdn.example/dlc.jpg", dlc.Cover);
        Assert.False(content!.IsGame);
        Assert.Null(content.Cover);

        // An answer that does not have the entry is no answer, so it is asked about again.
        Assert.Null(EpicAccountApi.ParseCatalogItem("{}", new EpicLibraryRecord("Lupin", "ns1", "item1")));
        Assert.Null(EpicAccountApi.ParseCatalogItem("not json", new EpicLibraryRecord("Lupin", "ns1", "item1")));
    }

    [Fact]
    public void The_launchers_log_says_when_it_refused_an_install_link()
    {
        // As the launcher wrote them on 2026-11-15, with the ids shortened.
        const string refused = """
            [2026.11.15-01.19.41:943][818]LogUriHandler: AppInstallUriHandler: Dispatching install for AppId ns1:item1:Lupin; will navigate to Library when the install-location selector concludes
            [2026.11.15-01.19.41:948][818]LogPortal: Error: FAlertReporter: Reporting Alert code: AI-NE
            """;
        const string stuck = """[2026.11.15-01.20.09:658][484]LogUriHandler: Warning: URI ["EpicGamesLauncher.exe" com.epicgames.launcher://apps/ns1%3Aitem1%3ALupin?action=install] is already being processed by FAppInstallUriHandler and it is not restartable""";
        const string fine = """
            [2026.11.15-01.24.49:000][131]LogUriHandler: AppInstallUriHandler: Dispatching install for AppId ns1:item1:Lupin; will navigate to Library when the install-location selector concludes
            [2026.11.15-01.24.55:671][331]LogUriHandler: AppInstallUriHandler: Install-location selector concluded for AppId ns1:item1:Lupin (accepted=true); navigating to Library
            """;

        Assert.True(EpicLauncherLog.Refused(refused));
        Assert.True(EpicLauncherLog.Refused(stuck));
        Assert.False(EpicLauncherLog.Refused(fine));
        Assert.False(EpicLauncherLog.Refused(string.Empty));
    }

    [Fact]
    public void Only_games_become_games_to_install_with_the_launchers_install_address()
    {
        var games = EpicAccountApi.AsGames(
        [
            new EpicOwnedGame("Lupin", "ns1", "item1", "Old Dungeon", "https://cdn.example/tall.jpg", IsGame: true),
            new EpicOwnedGame("Cellar", "ns1", "dlc1", "Old Dungeon: The Cellar", null, IsGame: false),
        ]);

        var game = Assert.Single(games);
        Assert.Equal("epic:Lupin", game.Key);
        Assert.Equal("Old Dungeon", game.Title);
        Assert.Equal(GameStore.Epic, game.Store);
        Assert.False(game.Installed);
        Assert.Equal("https://cdn.example/tall.jpg", game.CoverUrl);
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Aitem1%3ALupin?action=install", game.LaunchTarget);
    }
}
