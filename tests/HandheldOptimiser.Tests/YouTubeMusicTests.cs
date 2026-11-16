using System.Text.Json.Nodes;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The YouTube Music player's logic without a browser: which ids it will play, the requests it sends, the
/// sign-in hash, and reading songs and playlists out of YouTube Music's answers.
/// </summary>
public class YouTubeMusicTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf", true)]
    [InlineData("LM", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc&list=x", false)]
    [InlineData("abc'); alert(1); ('", false)]
    [InlineData("../../x", false)]
    public void Only_ids_youtube_makes_are_played(string? id, bool safe) =>
        Assert.Equal(safe, YouTubeMusic.IsSafeId(id));

    [Fact]
    public void A_song_and_a_playlist_play_from_their_own_address()
    {
        Assert.Equal("https://music.youtube.com/watch?v=dQw4w9WgXcQ", YouTubeMusic.PlayUrl(AutoTdpProtocol.Music.Song, "dQw4w9WgXcQ"));
        Assert.Equal("https://music.youtube.com/watch?list=LM", YouTubeMusic.PlayUrl(AutoTdpProtocol.Music.Playlist, "LM"));
        Assert.Null(YouTubeMusic.PlayUrl("album", "LM"));
        Assert.Null(YouTubeMusic.PlayUrl(AutoTdpProtocol.Music.Song, "x y"));
    }

    [Fact]
    public void The_sign_in_hash_is_the_time_and_a_sha1_of_the_time_cookie_and_origin()
    {
        // SHA-1 of "1700000000 abc/DEF https://music.youtube.com".
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes("1700000000 abc/DEF https://music.youtube.com")));

        Assert.Equal($"SAPISIDHASH 1700000000_{expected}", YouTubeMusic.AuthHeader("abc/DEF", 1700000000));
    }

    [Fact]
    public void Requests_carry_the_pages_context_or_a_plain_one()
    {
        var own = JsonNode.Parse(YouTubeMusic.SearchBody("""{"client":{"clientName":"WEB_REMIX","hl":"en-GB"}}""", "lofi"))!;
        Assert.Equal("en-GB", (string?)own["context"]!["client"]!["hl"]);
        Assert.Equal("lofi", (string?)own["query"]);

        foreach (var broken in new[] { null, "", "null", "{", "[1]", """{"other":1}""" })
        {
            var plain = JsonNode.Parse(YouTubeMusic.PlaylistsBody(broken))!;
            Assert.Equal("WEB_REMIX", (string?)plain["context"]!["client"]!["clientName"]);
            Assert.Equal("FEmusic_liked_playlists", (string?)plain["browseId"]);
        }
    }

    private const string Answer = """
        {"contents":{"tabs":[{"content":{"sections":[
          {"musicShelfRenderer":{"contents":[
            {"musicResponsiveListItemRenderer":{
              "flexColumns":[
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Blinding Lights","navigationEndpoint":{"watchEndpoint":{"videoId":"4NRXx6U8ABQ"}}}]}}},
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"The Weeknd"},{"text":" • "},{"text":"After Hours"}]}}}
              ],
              "menu":{"items":[{"navigationEndpoint":{"watchEndpoint":{"videoId":"otherOne123"}}}]}
            }},
            {"musicResponsiveListItemRenderer":{
              "flexColumns":[
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"The Weeknd","navigationEndpoint":{"browseEndpoint":{"browseId":"UC0WP5P-ufpRfjbNrmOWwLBQ"}}}]}}},
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist"}]}}}
              ]
            }},
            {"musicResponsiveListItemRenderer":{
              "flexColumns":[
                {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Blinding Lights","navigationEndpoint":{"watchEndpoint":{"videoId":"4NRXx6U8ABQ"}}}]}}}
              ]
            }}
          ]}},
          {"gridRenderer":{"items":[
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Liked music"}]},
              "subtitle":{"runs":[{"text":"Auto playlist"}]},
              "navigationEndpoint":{"browseEndpoint":{"browseId":"VLLM"}}
            }},
            {"musicTwoRowItemRenderer":{
              "title":{"runs":[{"text":"Gaming mix"}]},
              "subtitle":{"runs":[{"text":"Playlist"},{"text":" • "},{"text":"42 songs"}]},
              "thumbnailOverlay":{"musicItemThumbnailOverlayRenderer":{"content":{"musicPlayButtonRenderer":{
                "playNavigationEndpoint":{"watchPlaylistEndpoint":{"playlistId":"PLabc_DEF-123"}}}}}}
            }},
            {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"New playlist"}]}}}
          ]}}
        ]}}]}}
        """;

    [Fact]
    public void Songs_and_playlists_are_read_out_of_an_answer_once_each_in_order()
    {
        var items = YouTubeMusic.Items(Answer);

        Assert.Equal(
        [
            new MusicItem(AutoTdpProtocol.Music.Song, "4NRXx6U8ABQ", "Blinding Lights", "The Weeknd • After Hours"),
            new MusicItem(AutoTdpProtocol.Music.Playlist, "LM", "Liked music", "Auto playlist"),
            new MusicItem(AutoTdpProtocol.Music.Playlist, "PLabc_DEF-123", "Gaming mix", "Playlist • 42 songs")
        ], items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Bad"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLx'y"}}}}""")]
    public void An_answer_with_nothing_to_play_gives_no_items(string answer) =>
        Assert.Empty(YouTubeMusic.Items(answer));

    [Fact]
    public void A_long_answer_gives_no_more_than_the_most_items()
    {
        var rows = string.Join(",", Enumerable.Range(0, 40).Select(i =>
            """{"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"List X"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"VLPLX"}}}}"""
                .Replace("X", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(YouTubeMusic.MostItems, YouTubeMusic.Items($"[{rows}]").Count);
    }
}
