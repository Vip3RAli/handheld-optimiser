using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The game library's pure logic: reading Steam's config format, naming and listing ROM files, the
/// play time wording, game profiles, screen sizes, docked mode, Continue playing, the stores' lists
/// of owned games and which games Quick Resume may pause. Nothing here reads or writes the registry.
/// </summary>
public sealed class LibraryFeatureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ho-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryFeatureTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void KeyValues_ReadsSteamPlayTimes()
    {
        const string text = """
            "UserLocalConfigStore"
            {
                // A comment Steam never writes, but the format allows.
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "570"
                                {
                                    "LastPlayed"        "1700000000"
                                    "Playtime"          "1234"
                                    "cloud"
                                    {
                                        "last_sync_state"       "synchronized"
                                    }
                                }
                                "620" { "Playtime" "5" }
                            }
                            "Path"  "C:\\Program Files (x86)\\Steam"
                        }
                    }
                }
            }
            """;

        var root = KeyValues.Parse(text);
        var steam = root?.Path("UserLocalConfigStore", "Software", "Valve", "Steam");

        Assert.NotNull(steam);
        Assert.Equal("1234", steam!.Path("apps", "570")?.Value("Playtime"));
        Assert.Equal("1700000000", steam.Path("APPS", "570")?.Value("lastplayed"));
        Assert.Equal("5", steam.Path("apps", "620")?.Value("Playtime"));
        Assert.Equal(@"C:\Program Files (x86)\Steam", steam.Value("Path"));
        Assert.Equal(2, steam.Block("apps")!.Blocks.Count());
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("\"a\" }")]
    [InlineData("\"a\" { \"b\" }")]
    public void KeyValues_RejectsBrokenText(string text) => Assert.Null(KeyValues.Parse(text));

    [Theory]
    [InlineData("Super Metroid (Japan, USA) (En,Ja).sfc", "Super Metroid")]
    [InlineData("Chrono Trigger (USA) [!].smc", "Chrono Trigger")]
    [InlineData("Final Fantasy VII (USA) (Disc 1).chd", "Final Fantasy VII")]
    [InlineData("Tetris.gb", "Tetris")]
    [InlineData("(Prototype).nes", "(Prototype)")]
    public void Emulators_TitleDropsRegionAndDumpTags(string file, string title) =>
        Assert.Equal(title, Emulators.TitleOf(Path.Combine("C:", "roms", file)));

    [Theory]
    [InlineData("snes", "snes")]
    [InlineData("SFC", "snes")]
    [InlineData("megadrive", "genesis")]
    [InlineData("PS1", "psx")]
    [InlineData("gamecube", "gc")]
    public void Emulators_KnowsFolderNames(string folder, string system) =>
        Assert.Equal(system, Emulators.SystemFor(folder)?.Id);

    [Fact]
    public void Emulators_UnknownFolderIsNoSystem() => Assert.Null(Emulators.SystemFor("screenshots"));

    [Fact]
    public void Emulators_ListsEachDiscGameOnce()
    {
        var psx = Emulators.SystemFor("psx")!;
        Touch("Crash Bandicoot (USA).cue", "FILE \"Crash Bandicoot (USA).bin\" BINARY");
        Touch("Crash Bandicoot (USA).bin");
        Touch("Final Fantasy VII (Disc 1).chd");
        Touch("Final Fantasy VII (Disc 2).chd");
        Touch("Final Fantasy VII.m3u", "Final Fantasy VII (Disc 1).chd\nFinal Fantasy VII (Disc 2).chd");
        Touch("Spyro the Dragon (USA).chd");
        Touch("readme.txt");

        var games = Emulators.RomFiles(psx, _folder).Select(Path.GetFileName).ToList();

        Assert.Equal(["Crash Bandicoot (USA).cue", "Final Fantasy VII.m3u", "Spyro the Dragon (USA).chd"], games);
    }

    [Fact]
    public void Emulators_FindsGamesKeptInAFolderEach()
    {
        var ps2 = Emulators.SystemFor("ps2")!;
        Directory.CreateDirectory(Path.Combine(_folder, "Okami"));
        Touch(Path.Combine("Okami", "Okami (USA).iso"));

        Assert.Equal(["Okami (USA).iso"], Emulators.RomFiles(ps2, _folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(30, "1 min")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 5 min")]
    [InlineData(150 * 3600, "150 h")]
    public void PlayStats_DurationReadsNaturally(int seconds, string text) =>
        Assert.Equal(text, PlayStats.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void PlayStats_DescribesTimeAndLastPlay()
    {
        var now = DateTimeOffset.UtcNow;
        var stats = new PlayStats(TimeSpan.FromMinutes(90), now - TimeSpan.FromDays(3));

        Assert.Equal("1 h 30 min played, last played 3 days ago", stats.Describe(now));
        Assert.Null(default(PlayStats).Describe(now));
        Assert.Equal("Last played yesterday", new PlayStats(TimeSpan.Zero, now - TimeSpan.FromHours(30)).Describe(now));
    }

    [Fact]
    public void GameProfiles_KeepTheResolution()
    {
        var profile = new GameProfile(PowerMode.Performance, 120, 50, CloseApps: true, Resolution: new Resolution(1280, 720));
        var text = GameProfiles.Format(profile);

        Assert.Equal("power=performance;res=1280x720;hz=120;brightness=50;closeapps=on", text);
        Assert.Equal(profile, GameProfiles.Parse(text));
        Assert.Contains("1280 x 720, 120 Hz, 50% brightness", profile.Describe());
    }

    [Theory]
    [InlineData("res=1280x720", 1280, 720)]
    [InlineData("res=1920X1080;hz=60", 1920, 1080)]
    [InlineData("res=wide", null, null)]
    [InlineData("res=0x720", null, null)]
    public void GameProfiles_ReadTheResolution(string text, int? width, int? height) =>
        Assert.Equal(width is null ? null : new Resolution(width.Value, height!.Value), GameProfiles.Parse(text).Resolution);

    [Fact]
    public void GameProfiles_ResolutionAloneIsNotEmpty() =>
        Assert.False(new GameProfile(null, null, null, Resolution: new Resolution(1280, 720)).IsEmpty);

    [Fact]
    public void Resolutions_OfferSizesTheShapeOfTheScreen()
    {
        Resolution[] offered =
        [
            new(1920, 1080), new(1680, 1050), new(1600, 900), new(1366, 768), new(1280, 800),
            new(1280, 720), new(1024, 768), new(800, 600), new(640, 480), new(1920, 1080)
        ];

        var choices = Resolutions.Choices(offered, new Resolution(1920, 1080));

        Assert.Equal([new(1920, 1080), new(1600, 900), new(1366, 768), new(1280, 720)], choices);
    }

    [Fact]
    public void Resolutions_KeepTheCurrentSizeAmongTheChoices()
    {
        var choices = Resolutions.Choices([new(1920, 1080), new(1280, 720)], new Resolution(1280, 800));

        Assert.Contains(new Resolution(1280, 800), choices);
        Assert.Equal(new Resolution(1920, 1080), choices[0]);
    }

    [Fact]
    public void Displays_DockedOnlyWhenTheScreenShowsOnSomethingPluggedIn()
    {
        const uint hdmi = 5;
        const uint displayPortEmbedded = 11;
        (string, uint)[] handheld = [(@"\\.\DISPLAY1", displayPortEmbedded)];
        (string, uint)[] duplicated = [(@"\\.\DISPLAY1", displayPortEmbedded), (@"\\.\DISPLAY1", hdmi)];
        (string, uint)[] extended = [(@"\\.\DISPLAY1", displayPortEmbedded), (@"\\.\DISPLAY2", hdmi)];

        Assert.False(Displays.IsDocked(handheld, @"\\.\DISPLAY1"));
        Assert.True(Displays.IsDocked(duplicated, @"\\.\DISPLAY1"));
        Assert.False(Displays.IsDocked(extended, @"\\.\DISPLAY1"));
        Assert.True(Displays.IsDocked(extended, @"\\.\display2"));
    }

    [Theory]
    [InlineData(0, 1280, 1)]
    [InlineData(0, 1920, 1.5)]
    [InlineData(0, 2560, 2)]
    [InlineData(0, 800, 1)]
    [InlineData(125, 1920, 1.25)]
    public void Displays_ScaleFitsTheHandheldsLayout(int size, double width, double scale) =>
        Assert.Equal(scale, Displays.Scale(size, width), 3);

    [Fact]
    public void RecentGames_PicksThePlayedOnesNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var played = new Dictionary<string, DateTimeOffset?>
        {
            ["a"] = now - TimeSpan.FromDays(3),
            ["b"] = null,
            ["c"] = now,
            ["d"] = now - TimeSpan.FromDays(1)
        };

        Assert.Equal(["c", "d"], RecentGames.Pick(played.Keys, k => played[k], 2));
        Assert.Equal(["c", "d", "a"], RecentGames.Pick(played.Keys, k => played[k], 10));
        Assert.Empty(RecentGames.Pick(played.Keys, k => played[k], 0));
    }

    [Fact]
    public void OwnedGames_ReadsEpicsCatalogueCache()
    {
        const string json = """
            [
              { "id": "item1", "namespace": "ns1", "title": "Hades",
                "categories": [ { "path": "games" }, { "path": "applications" } ],
                "releaseInfo": [ { "appId": "Min", "platform": [ "Windows", "Mac" ] } ],
                "keyImages": [ { "type": "Thumbnail", "url": "https://cdn1.epicgames.com/t.jpg" },
                               { "type": "DieselGameBoxTall", "url": "https://cdn1.epicgames.com/tall.jpg" } ] },
              { "id": "item2", "namespace": "ns1", "title": "Hades Soundtrack",
                "categories": [ { "path": "addons" }, { "path": "games" } ],
                "mainGameItem": { "id": "item1" },
                "releaseInfo": [ { "appId": "MinOst", "platform": [ "Windows" ] } ] },
              { "id": "item3", "namespace": "ns2", "title": "Mac Only",
                "categories": [ { "path": "games" } ],
                "releaseInfo": [ { "appId": "Mac1", "platform": [ "Mac" ] } ] },
              { "id": "item4", "namespace": "ns3", "title": "Unreal Engine",
                "categories": [ { "path": "engines" } ],
                "releaseInfo": [ { "appId": "UE", "platform": [ "Windows" ] } ] }
            ]
            """;

        var games = OwnedGames.ParseEpicCatalog(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));

        var hades = Assert.Single(games);
        Assert.Equal("epic:Min", hades.Key);
        Assert.Equal("Hades", hades.Title);
        Assert.False(hades.Installed);
        Assert.Equal("https://cdn1.epicgames.com/tall.jpg", hades.CoverUrl);
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Aitem1%3AMin?action=install", hades.LaunchTarget);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("e30=")]
    public void OwnedGames_BrokenEpicCacheListsNothing(string text) => Assert.Empty(OwnedGames.ParseEpicCatalog(text));

    [Fact]
    public void OwnedGames_ReadsSteamsAnswer()
    {
        const string json = """
            { "response": { "game_count": 3, "games": [
                { "appid": 620, "name": "Portal 2", "playtime_forever": 600 },
                { "appid": 228980, "name": "Steamworks Common Redistributables" },
                { "appid": 400 }
            ] } }
            """;

        var portal = Assert.Single(OwnedGames.ParseSteamOwned(json, artDir: null));
        Assert.Equal("steam:620", portal.Key);
        Assert.Equal("steam://install/620", portal.LaunchTarget);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/steam/apps/620/library_600x900.jpg", portal.CoverUrl);
        Assert.Empty(OwnedGames.ParseSteamOwned("""{ "response": {} }""", null));
    }

    [Fact]
    public void OwnedGames_ReadsGogGalaxyRows()
    {
        string?[][] rows =
        [
            ["gog_1207658924", "title", """{"title":"The Witcher 3: Wild Hunt"}"""],
            ["gog_1207658924", "originalImages", """{"verticalCover":"https://images.gog.com/abc_glx_vertical_cover.webp"}"""],
            ["gog_42", "originalTitle", """{"title":"Original Only"}"""],
            ["steam_620", "title", """{"title":"Portal 2"}"""],
            ["gog_7", "title", "not json"]
        ];

        var games = OwnedGames.ParseGalaxyRows(rows).OrderBy(g => g.Key).ToList();

        Assert.Equal(["gog:1207658924", "gog:42"], games.Select(g => g.Key));
        Assert.Equal("The Witcher 3: Wild Hunt", games[0].Title);
        Assert.Equal("https://images.gog.com/abc_glx_vertical_cover.jpg", games[0].CoverUrl);
        Assert.Equal("goggalaxy://openGameView/1207658924", games[0].LaunchTarget);
        Assert.Equal("Original Only", games[1].Title);
    }

    [Fact]
    public void GamePause_NoteOfPausedProcessesRoundTrips()
    {
        var processes = new List<(int Id, long Started)> { (1234, 133700000000000000), (5678, 133700000000000001) };

        var text = GamePause.Format(processes);

        Assert.Equal("1234@133700000000000000;5678@133700000000000001", text);
        Assert.Equal(processes, GamePause.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12@")]
    [InlineData("@5")]
    [InlineData("-1@5;0@5;7@-3")]
    public void GamePause_BrokenNoteResumesNothing(string? text) => Assert.Empty(GamePause.Parse(text));

    [Fact]
    public void GamePause_BrokenEntriesAreSkipped() =>
        Assert.Equal([(42, 99L)], GamePause.Parse("x@1; 42@99 ;3@y"));

    [Fact]
    public void GamePause_OnlyAGamesOwnFolderIsPaused()
    {
        var programFiles = Path.Combine(_folder, "Program Files");
        var windows = Path.Combine(_folder, "Windows");
        string[] system = [programFiles];

        Assert.False(GamePause.TooBroad(Path.Combine(programFiles, "Steam", "steamapps", "common", "Hades"), system, windows));
        Assert.True(GamePause.TooBroad(programFiles, system, windows));
        Assert.True(GamePause.TooBroad(programFiles + Path.DirectorySeparatorChar, system, windows));
        Assert.True(GamePause.TooBroad(_folder, system, windows));
        Assert.True(GamePause.TooBroad(Path.Combine(windows, "System32"), system, windows));
        Assert.True(GamePause.TooBroad(Path.GetPathRoot(_folder)!, system, windows));
        Assert.True(GamePause.TooBroad("", system, windows));
    }

    [Fact]
    public void GamePause_FindsAntiCheatInTheGamesFolder()
    {
        var game = Directory.CreateDirectory(Path.Combine(_folder, "Fortnite")).FullName;
        Assert.Null(GamePause.FindAntiCheat(game));

        var win64 = Directory.CreateDirectory(Path.Combine(game, "FortniteGame", "Binaries", "Win64")).FullName;
        File.WriteAllText(Path.Combine(win64, "FortniteClient-Win64-Shipping.exe"), "");
        Assert.Null(GamePause.FindAntiCheat(game));

        Directory.CreateDirectory(Path.Combine(win64, "EasyAntiCheat"));
        Assert.Equal("Easy Anti-Cheat", GamePause.FindAntiCheat(game));
    }

    [Fact]
    public void GamePause_FindsAntiCheatByItsFiles()
    {
        var game = Directory.CreateDirectory(Path.Combine(_folder, "DayZ")).FullName;
        File.WriteAllText(Path.Combine(game, "BEService_x64.exe"), "");

        Assert.Equal("BattlEye", GamePause.FindAntiCheat(game));
        Assert.Null(GamePause.FindAntiCheat(Path.Combine(_folder, "missing")));
    }

    private void Touch(string name, string text = "")
    {
        File.WriteAllText(Path.Combine(_folder, name), text);
    }
}
