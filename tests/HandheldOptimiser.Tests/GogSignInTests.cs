using System.IO;
using System.IO.Compression;
using System.Text;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Signing in to GOG inside the library, and what is asked of GOG afterwards. The answers are laid out as GOG
/// gave them to the sign-in probe on 2026-11-14, with the ids, names, secrets and tokens made up.
/// </summary>
public class GogSignInTests
{
    [Fact]
    public void The_code_is_taken_from_the_page_GOG_goes_to_once_signed_in()
    {
        Assert.Equal("abc123", GogAuth.CodeFrom(new Uri("https://embed.gog.com/on_login_success?origin=client&code=abc123")));
        Assert.Null(GogAuth.CodeFrom(new Uri("https://embed.gog.com/on_login_success?origin=client")));
        Assert.Null(GogAuth.CodeFrom(new Uri("https://login.gog.com/login?code=abc123")));
        Assert.Null(GogAuth.CodeFrom(new Uri("https://evil.example/on_login_success?code=abc123")));
    }

    [Fact]
    public void The_sign_in_page_is_for_Galaxys_client_and_comes_back_to_GOG()
    {
        var url = GogAuth.SignInUrl;

        Assert.StartsWith("https://auth.gog.com/auth?client_id=46899977096215655", url);
        Assert.Contains(Uri.EscapeDataString(GogAuth.Redirect), url);
        Assert.Contains("response_type=code", url);
    }

    [Fact]
    public void A_token_answer_gives_both_tokens_the_account_and_when_it_runs_out()
    {
        const string answer = """
            {"access_token":"access-1","expires_in":3600,"token_type":"bearer","scope":"","session_id":"s","refresh_token":"refresh-1","user_id":"48000000000000001"}
            """;
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        var tokens = GogAuth.ParseTokens(answer, now);

        Assert.NotNull(tokens);
        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal("48000000000000001", tokens.UserId);
        Assert.Equal(now.AddHours(1), tokens.Expires);
    }

    [Fact]
    public void An_error_answer_has_no_tokens()
    {
        Assert.Null(GogAuth.ParseTokens("""{"error":"invalid_grant","error_description":"Invalid refresh token"}""", DateTimeOffset.UtcNow));
        Assert.Null(GogAuth.ParseTokens("not json", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Only_the_name_is_read_from_the_account_never_the_email()
    {
        const string answer = """
            {"country":"NZ","isLoggedIn":true,"userId":"48000000001","username":"Player1","galaxyUserId":"48000000000000001","email":"someone@example.com"}
            """;

        Assert.Equal("Player1", GogAccountApi.ParseUserName(answer));
    }

    [Fact]
    public void The_shelf_lists_Windows_games_and_says_how_many_pages_there_are()
    {
        const string answer = """
            {"page":1,"totalProducts":3,"totalPages":2,"products":[
              {"id":1207660001,"title":"Old Dungeon","worksOn":{"Windows":true,"Mac":true,"Linux":true},"isGame":true},
              {"id":2018300002,"title":"Mac Only Thing","worksOn":{"Windows":false,"Mac":true,"Linux":false},"isGame":true},
              {"id":1207660003,"title":"Soundtrack","worksOn":{"Windows":true},"isGame":false}
            ]}
            """;

        var (games, pages) = GogAccountApi.ParseFilteredProducts(answer);

        Assert.Equal(2, pages);
        var game = Assert.Single(games);
        Assert.Equal("1207660001", game.Id);
        Assert.Equal("Old Dungeon", game.Title);
    }

    [Fact]
    public void A_cover_is_the_portrait_from_the_games_database_as_a_JPEG()
    {
        const string answer = """
            {"id":"1","game":{"vertical_cover":{"url_format":"https://images.gog.com/abc{formatter}.{ext}?namespace=gamesdb"}}}
            """;

        Assert.Equal("https://images.gog.com/abc_glx_vertical_cover.jpg?namespace=gamesdb", GogAccountApi.ParseCover(answer));
        Assert.Null(GogAccountApi.ParseCover("""{"game":{}}"""));
    }

    [Fact]
    public void A_game_from_the_account_has_the_key_Galaxy_gives_the_same_game()
    {
        var fromAccount = Assert.Single(GogAccountApi.AsGames([new GogOwnedGame("1207660001", "Old Dungeon", "https://images.gog.com/c.jpg")]));
        var fromGalaxy = Assert.Single(OwnedGames.ParseGalaxyRows([["gog_1207660001", "title", """{"title":"Old Dungeon"}"""]]));

        Assert.Equal(fromGalaxy.Key, fromAccount.Key);
        Assert.Equal("gog:1207660001", fromAccount.Key);
        Assert.Equal(GameStore.Gog, fromAccount.Store);
        Assert.False(fromAccount.Installed);
        Assert.Equal("https://images.gog.com/c.jpg", fromAccount.CoverUrl);
    }

    [Fact]
    public void The_kept_list_reads_back_as_it_was_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gog-owned-{Guid.NewGuid():N}.json");
        try
        {
            List<GogOwnedGame> games = [new("1", "A", null), new("2", "B", "https://images.gog.com/b.jpg")];
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(games));

            Assert.Equal(games, GogAccountApi.ReadOwned(path));
            Assert.Empty(GogAccountApi.ReadOwned(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Achievements_say_which_are_unlocked_and_when()
    {
        const string answer = """
            {"total_count":3,"limit":1000,"items":[
              {"achievement_id":"1","achievement_key":"FirstBlood","visible":true,"name":"First blood","description":"Win a fight.","date_unlocked":"2025-03-01T10:20:30+0000"},
              {"achievement_id":"2","achievement_key":"Backstab","visible":false,"name":"Just the tip","description":"Perform 3 backstabs.","date_unlocked":null},
              {"achievement_id":"3","achievement_key":"Campaign","visible":true,"name":"Here we go","description":"Start the campaign.","date_unlocked":null}
            ],"achievements_mode":"all_visible"}
            """;

        var achievements = GogAccountApi.ParseAchievements("gog:2018300002", answer);

        Assert.NotNull(achievements);
        Assert.Equal(3, achievements.Total);
        Assert.Equal(1, achievements.Unlocked);
        var first = achievements.Items.Single(i => i.Id == "FirstBlood");
        Assert.True(first.Unlocked);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 10, 20, 30, TimeSpan.Zero), first.UnlockedAt);
    }

    [Fact]
    public void A_game_with_no_achievements_has_none_to_show()
    {
        Assert.Null(GogAccountApi.ParseAchievements("gog:1", """{"total_count":0,"limit":1000,"items":[],"achievements_mode":"all_visible"}"""));
    }

    [Fact]
    public void Builds_are_the_public_current_format_ones_newest_first()
    {
        const string answer = """
            {"total_count":4,"count":4,"items":[
              {"build_id":"500","product_id":"1207660001","os":"windows","branch":null,"version_name":"1.01","public":true,"date_published":"2023-01-01T00:00:00+0000","generation":2,"link":"https://gog-cdn.example/meta/aa/bb/old"},
              {"build_id":"600","product_id":"1207660001","os":"windows","branch":null,"version_name":"1.02 CS","public":true,"date_published":"2024-11-07T15:38:52+0000","generation":2,"link":"https://gog-cdn.example/meta/cc/dd/new"},
              {"build_id":"700","product_id":"1207660001","os":"windows","branch":"beta","version_name":"1.03b","public":true,"date_published":"2025-01-01T00:00:00+0000","generation":2,"link":"https://gog-cdn.example/meta/ee/ff/beta"},
              {"build_id":"13761","product_id":"1207660001","os":"windows","branch":null,"version_name":"","public":true,"date_published":"2015-04-29T14:36:19+0000","generation":1,"link":"https://gog-cdn.example/v1/repository.json","legacy_build_id":36689779}
            ]}
            """;

        var builds = GogContent.ParseBuilds(answer);

        Assert.Equal(["600", "500"], builds.Select(b => b.BuildId));
        Assert.Equal("1.02 CS", builds[0].Version);
        Assert.Equal("https://gog-cdn.example/meta/cc/dd/new", builds[0].Link);
    }

    private const string ManifestJson = """
        {"version":2,"baseProductId":"1207660001","platform":"windows",
         "offlineDepot":{"manifest":"b6bf67562edf83a2d5e3ea6d14641fa7","size":5176,"compressedSize":906,"productId":"1207660001","languages":["*"]},
         "depots":[
           {"manifest":"479f75573216e7a9b619463992cb8534","size":4385433,"compressedSize":3037451,"productId":"1207660001","languages":["en-US"]},
           {"manifest":"83f9f72f20938fc493fef40d0dadec79","size":872,"compressedSize":845,"productId":"1207660001","languages":["en-US"],"isGogDepot":true},
           {"manifest":"526f4984a598ce2f916bc300b5d7906a","size":1841,"compressedSize":383,"productId":"1207660001","languages":["*"],"isGogDepot":true}
         ],
         "products":[{"name":"Old Dungeon","productId":"1207660001","temp_executable":"","temp_arguments":""}],
         "dependencies":["DOSBox074_2CS"],"buildId":"600","installDirectory":"Old Dungeon","scriptInterpreter":true,
         "clientId":"49960000000000001","clientSecret":"made-up-secret"}
        """;

    [Fact]
    public void A_build_manifest_gives_its_depots_folder_and_the_games_own_client()
    {
        var manifest = GogContent.ParseManifest(ManifestJson);

        Assert.NotNull(manifest);
        Assert.Equal("1207660001", manifest.BaseProductId);
        Assert.Equal("600", manifest.BuildId);
        Assert.Equal("Old Dungeon", manifest.InstallDirectory);
        Assert.Equal(3, manifest.Depots.Count);
        Assert.Equal(["en-US"], manifest.Depots[0].Languages);
        Assert.True(manifest.Depots[1].IsGogDepot);
        Assert.Equal("b6bf67562edf83a2d5e3ea6d14641fa7", manifest.OfflineDepot?.Manifest);
        Assert.Equal(["DOSBox074_2CS"], manifest.Dependencies);
        Assert.Equal("49960000000000001", manifest.ClientId);
        Assert.Equal("made-up-secret", manifest.ClientSecret);
    }

    [Fact]
    public void Manifests_packed_with_zlib_are_unpacked_and_plain_ones_read_as_they_are()
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(ManifestJson));
        }

        Assert.Equal(ManifestJson, GogContent.Unzlib(packed.ToArray()));
        Assert.Equal(ManifestJson, GogContent.Unzlib(Encoding.UTF8.GetBytes(ManifestJson)));
    }

    [Fact]
    public void A_manifest_is_found_by_its_hash()
    {
        Assert.Equal("https://cdn.gog.com/content-system/v2/meta/47/9f/479f75573216e7a9b619463992cb8534",
            GogContent.MetaUrl("479f75573216e7a9b619463992cb8534"));
    }
}
