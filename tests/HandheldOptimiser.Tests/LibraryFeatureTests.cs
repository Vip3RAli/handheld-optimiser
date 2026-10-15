using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The game library's pure logic: reading Steam's config format, naming and listing ROM files, the
/// play time wording, game profiles, screen sizes, docked mode, Last played, store details, the stores' lists
/// of owned games, which games Quick Resume may pause and which added programs are apps. Nothing here
/// reads or writes the registry.
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

    [Theory]
    [InlineData("fps=60", 60)]
    [InlineData("power=balanced;fps=off", 0)]
    [InlineData("fps=55", null)]
    [InlineData("hz=60", null)]
    public void GameProfiles_ReadTheAutoTdpFrameRate(string text, int? fps)
    {
        var profile = GameProfiles.Parse(text);

        Assert.Equal(fps, profile.AutoTdpFps);
        Assert.Equal(profile, GameProfiles.Parse(GameProfiles.Format(profile)));
    }

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

    [Theory]
    [InlineData("The Witcher® 3: Wild Hunt", "witcher", true)]
    [InlineData("The Witcher® 3: Wild Hunt", "WITCHER 3", true)]
    [InlineData("The Witcher® 3: Wild Hunt", "hunt wild", true)]
    [InlineData("The Witcher® 3: Wild Hunt", "wit", true)]
    [InlineData("The Witcher® 3: Wild Hunt", "witcher 2", false)]
    [InlineData("Baldur's Gate 3", "baldurs", true)]
    [InlineData("Baldur's Gate 3", "", true)]
    [InlineData("Baldur's Gate 3", "  ", true)]
    [InlineData("Baldur's Gate 3", "gate x", false)]
    public void GameSearch_FindsEveryWordInTheTitle(string title, string search, bool found) =>
        Assert.Equal(found, GameSearch.Matches(title, search));

    [Fact]
    public void StoreDetails_ReadsSteamsAnswer()
    {
        const string json = """
            {
                "620": {
                    "success": true,
                    "data": {
                        "short_description": "The &quot;perpetual testing initiative&quot; has been expanded.",
                        "developers": ["Valve"],
                        "genres": [{ "id": "1", "description": "Action" }, { "id": "25", "description": "Adventure" }],
                        "release_date": { "coming_soon": false, "date": "18 Apr, 2011" }
                    }
                },
                "999": { "success": false }
            }
            """;

        var details = StoreDetails.ParseDetails(json, "620");

        Assert.NotNull(details);
        Assert.Equal("The \"perpetual testing initiative\" has been expanded.", details.Description);
        Assert.Equal(["Action", "Adventure"], details.Genres);
        Assert.Equal("18 Apr, 2011", details.Released);
        Assert.Equal("Valve", details.Developer);

        Assert.Null(StoreDetails.ParseDetails(json, "999"));
        Assert.Null(StoreDetails.ParseDetails(json, "1"));
        Assert.Null(StoreDetails.ParseDetails("not json", "620"));
    }

    [Fact]
    public void StoreDetails_OnlyTakesAnExactTitleFromASearch()
    {
        const string json = """
            {
                "total": 2,
                "items": [
                    { "type": "app", "name": "Cyberpunk 2077: Phantom Liberty", "id": 2138330 },
                    { "type": "app", "name": "Cyberpunk 2077", "id": 1091500 }
                ]
            }
            """;

        Assert.Equal("1091500", StoreDetails.ParseSearch(json, "Cyberpunk 2077"));
        Assert.Equal("1091500", StoreDetails.ParseSearch(json, "CYBERPUNK 2077®"));
        Assert.Null(StoreDetails.ParseSearch(json, "Cyberpunk"));
        Assert.Null(StoreDetails.ParseSearch(json, "™"));
        Assert.Null(StoreDetails.ParseSearch("{}", "Cyberpunk 2077"));
    }

    [Fact]
    public void StoreDetails_ReadsWikidataAndWikipedia()
    {
        const string search = """
            {
                "search": [
                    { "id": "Q1", "label": "Fortnite", "description": "American media franchise" },
                    { "id": "Q349375", "label": "Fortnite", "description": "2017 video game developed by Epic Games" },
                    { "id": "Q55577848", "label": "Fortnite: Save the World", "description": "co-op sandbox survival game" }
                ]
            }
            """;

        Assert.Equal("Q349375", StoreDetails.ParseWikidataSearch(search, "Fortnite®"));
        Assert.Null(StoreDetails.ParseWikidataSearch(search, "Fortnite 2"));
        Assert.Null(StoreDetails.ParseWikidataSearch("{}", "Fortnite"));

        const string entry = """
            {
                "entities": {
                    "Q349375": {
                        "claims": {
                            "P136": [
                                { "mainsnak": { "datavalue": { "value": { "id": "Q10" } } } },
                                { "mainsnak": { "snaktype": "novalue" } }
                            ],
                            "P178": [{ "mainsnak": { "datavalue": { "value": { "id": "Q20" } } } }],
                            "P577": [
                                { "mainsnak": { "datavalue": { "value": { "time": "+2020-11-10T00:00:00Z", "precision": 11 } } } },
                                { "mainsnak": { "datavalue": { "value": { "time": "+2017-07-25T00:00:00Z", "precision": 11 } } } }
                            ]
                        },
                        "sitelinks": { "enwiki": { "site": "enwiki", "title": "Fortnite" } }
                    }
                }
            }
            """;

        var read = StoreDetails.ParseWikidataEntry(entry, "Q349375");

        Assert.NotNull(read);
        Assert.Equal(["Q10"], read.Genres);
        Assert.Equal(["Q20"], read.Developers);
        Assert.Equal("25 Jul 2017", read.Released);
        Assert.Equal("Fortnite", read.Article);
        Assert.Null(StoreDetails.ParseWikidataEntry(entry, "Q2"));

        const string labels = """
            { "entities": { "Q10": { "labels": { "en": { "language": "en", "value": "survival game" } } }, "Q20": { "labels": { "mul": { "language": "mul", "value": "Epic Games" } } }, "Q30": { "labels": {} } } }
            """;

        var names = StoreDetails.ParseWikidataLabels(labels);

        Assert.Equal(2, names.Count);
        Assert.Equal("survival game", names["Q10"]);
        Assert.Equal("Epic Games", names["Q20"]);

        Assert.Equal("Fortnite is an online video game.",
            StoreDetails.ParseWikipediaSummary("""{ "type": "standard", "extract": "Fortnite is an online video game. " }"""));
        Assert.Null(StoreDetails.ParseWikipediaSummary("""{ "type": "disambiguation", "extract": "Fortnite may refer to:" }"""));
    }

    [Fact]
    public void OwnedGames_ReadsEpicsCatalogueCache()
    {
        const string json = """
            [
              { "id": "item1", "namespace": "ns1", "title": "Hades",
                "categories": [ { "path": "games" }, { "path": "applications" } ],
                "mainGameItem": { "namespace": "", "id": "" },
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

    [Fact]
    public void EpicFreeGames_ReadsTheGiveawaysOnly()
    {
        const string json = """
            { "data": { "Catalog": { "searchStore": { "elements": [
                { "title": "On Sale", "id": "a", "offerType": "BASE_GAME", "productSlug": "on-sale",
                  "price": { "totalPrice": { "fmtPrice": { "originalPrice": "$59.95" } } },
                  "promotions": { "promotionalOffers": [],
                    "upcomingPromotionalOffers": [ { "promotionalOffers": [
                      { "startDate": "2026-10-12T17:00:00.000Z", "endDate": "2027-01-07T23:59:00.000Z",
                        "discountSetting": { "discountType": "PERCENTAGE", "discountPercentage": 20 } } ] } ] } },
                { "title": "Free Next Week", "id": "b", "offerType": "BASE_GAME", "urlSlug": "free-next-week",
                  "keyImages": [ { "type": "Thumbnail", "url": "https://cdn1.epicgames.com/thumb.jpg" } ],
                  "promotions": { "promotionalOffers": [],
                    "upcomingPromotionalOffers": [ { "promotionalOffers": [
                      { "startDate": "2026-10-15T15:00:00.000Z", "endDate": "2026-10-22T15:00:00.000Z",
                        "discountSetting": { "discountType": "PERCENTAGE", "discountPercentage": 0 } } ] } ] } },
                { "title": "Free Now", "id": "c", "offerType": "BUNDLE", "productSlug": "[]",
                  "offerMappings": [ { "pageSlug": "free-now-b96ca8", "pageType": "productHome" } ],
                  "keyImages": [ { "type": "OfferImageTall", "url": "https://cdn1.epicgames.com/tall.jpg" },
                                 { "type": "OfferImageWide", "url": "https://cdn1.epicgames.com/wide.jpg" } ],
                  "price": { "totalPrice": { "fmtPrice": { "originalPrice": "$21.95", "discountPrice": "0" } } },
                  "promotions": { "promotionalOffers": [ { "promotionalOffers": [
                      { "startDate": "2026-10-08T15:00:00.000Z", "endDate": "2026-10-15T15:00:00.000Z",
                        "discountSetting": { "discountType": "PERCENTAGE", "discountPercentage": 0 } } ] } ],
                    "upcomingPromotionalOffers": [] } },
                { "title": "Free Before", "id": "d", "promotions": null }
            ] } } } }
            """;

        var deals = EpicFreeGames.Parse(json, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(["Free Now", "Free Next Week"], deals.Select(d => d.Title));

        var now = deals[0];
        Assert.Equal("epic-free:c", now.Id);
        Assert.Equal("Free", now.Price);
        Assert.Equal("$21.95", now.Regular);
        Assert.Equal(100, now.Cut);
        Assert.Equal("Free this week", now.Note);
        Assert.Equal("https://store.epicgames.com/bundles/free-now-b96ca8", now.Url);
        Assert.Equal("https://cdn1.epicgames.com/wide.jpg", now.Image);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero), now.Expiry);

        var next = deals[1];
        Assert.Equal("https://store.epicgames.com/p/free-next-week", next.Url);
        Assert.StartsWith("Free from ", next.Note);
        Assert.Null(next.Expiry);

        // A week on, the first giveaway is over and the second has begun.
        var later = Assert.Single(EpicFreeGames.Parse(json, new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Free Next Week", later.Title);
        Assert.Equal("Free this week", later.Note);

        Assert.Empty(EpicFreeGames.Parse("{}", DateTimeOffset.UtcNow));
        Assert.Empty(EpicFreeGames.Parse("not json", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0x28)]
    [InlineData(0x29)]
    public void SteamAppInfo_ReadsTheSteamAppsCaches(int version)
    {
        // From version 29 an entry is named by its place in a table at the end of the file.
        var names = new List<string>();
        byte[] Name(string name)
        {
            if (version < 0x29)
            {
                return [.. System.Text.Encoding.UTF8.GetBytes(name), 0];
            }

            if (!names.Contains(name))
            {
                names.Add(name);
            }

            return BitConverter.GetBytes(names.IndexOf(name));
        }

        byte[] Text(string name, string value) => [1, .. Name(name), .. System.Text.Encoding.UTF8.GetBytes(value), 0];
        byte[] Number(string name, int value) => [2, .. Name(name), .. BitConverter.GetBytes(value)];
        byte[] Group(string name, params byte[][] entries) => [0, .. Name(name), .. entries.SelectMany(e => e), 8];

        byte[] App(uint id, string name, string type)
        {
            byte[] values = [.. Group("appinfo", Number("appid", (int)id), Group("common", Text("name", name), Text("type", type))), 8];
            byte[] header = new byte[4 + 4 + 8 + 20 + 4 + 20];
            return [.. BitConverter.GetBytes(id), .. BitConverter.GetBytes(header.Length + values.Length), .. header, .. values];
        }

        byte[] apps = [.. App(620, "Portal 2", "Game"), .. App(1001, "Portal 2 Soundtrack", "DLC"), .. App(777, "Never Bought", "game"), 0, 0, 0, 0];
        byte[] table = [.. BitConverter.GetBytes(names.Count), .. names.SelectMany(n => (byte[])[.. System.Text.Encoding.UTF8.GetBytes(n), 0])];
        byte[] appInfo = version < 0x29
            ? [(byte)version, 0x44, 0x56, 0x07, 1, 0, 0, 0, .. apps]
            : [(byte)version, 0x44, 0x56, 0x07, 1, 0, 0, 0, .. BitConverter.GetBytes(16L + apps.Length), .. apps, .. table];

        var read = SteamAppInfo.ParseApps(appInfo);

        Assert.Equal([620L, 1001L, 777L], read.Select(a => a.AppId));
        Assert.Equal("Portal 2", read[0].Name);
        Assert.Equal(["game", "dlc", "game"], read.Select(a => a.Type));
        Assert.Empty(SteamAppInfo.ParseApps(appInfo[..^9]));
        Assert.Empty(SteamAppInfo.ParseApps([1, 2, 3]));

        // The packages are always named in full. Package 0 is the one everybody has.
        version = 0x28;
        byte[] Package(uint id, params int[] appIds) =>
        [
            .. BitConverter.GetBytes(id), .. new byte[20 + 4 + 8],
            .. Group(id.ToString(), Number("packageid", (int)id),
                Group("appids", appIds.Select((app, i) => Number(i.ToString(), app)).ToArray())),
            8
        ];

        byte[] packageInfo = [0x28, 0x55, 0x56, 0x06, 1, 0, 0, 0, .. Package(0, 7, 777), .. Package(54029, 620, 1001), 0xFF, 0xFF, 0xFF, 0xFF];

        var licensed = SteamAppInfo.ParsePackageApps(packageInfo);

        Assert.Equal([620L, 1001L], licensed.Order());

        var portal = Assert.Single(OwnedGames.SteamGamesFromCache(read, licensed, artDir: null));
        Assert.Equal("steam:620", portal.Key);
        Assert.Equal("Portal 2", portal.Title);
        Assert.Equal("steam://install/620", portal.LaunchTarget);
        Assert.False(portal.Installed);
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

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\MSEDGE.EXE")]
    [InlineData(@"C:\Users\Zain\AppData\Local\Discord\Update.exe")]
    [InlineData(@"C:\Users\Zain\AppData\Roaming\Spotify\Spotify.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    public void AddedPrograms_KnowsCommonApps(string exe) => Assert.True(AddedPrograms.LooksLikeApp(exe));

    [Theory]
    [InlineData(@"D:\Games\Hollow Knight\hollow_knight.exe")]
    [InlineData(@"C:\Emulators\RetroArch\retroarch.exe")]
    [InlineData(@"C:\Games\Discordia\Discordia.exe")]
    [InlineData(null)]
    public void AddedPrograms_TreatsTheRestAsGames(string? exe) => Assert.False(AddedPrograms.LooksLikeApp(exe));

    [Fact]
    public void OldSchoolStack_FrontCardIsFullSizeAtTheLeft()
    {
        var (scale, left, opacity) = OldSchoolStack.Place(0);

        Assert.Equal(1, scale);
        Assert.Equal(0, left);
        Assert.Equal(1, opacity);
    }

    [Fact]
    public void OldSchoolStack_CardsBehindGetSmallerAndFurtherRight()
    {
        var behind = Enumerable.Range(0, 6).Select(depth => OldSchoolStack.Place(depth)).ToList();

        for (var i = 1; i < behind.Count; i++)
        {
            Assert.True(behind[i].Scale < behind[i - 1].Scale);
            Assert.True(behind[i].Left > behind[i - 1].Left);

            // Each is tucked behind the one before: its right edge is further right, its left edge is not past that one's.
            Assert.True(behind[i].Left < behind[i - 1].Left + (behind[i - 1].Scale * OldSchoolStack.CardWidth));
            Assert.True(behind[i].Left + (behind[i].Scale * OldSchoolStack.CardWidth)
                > behind[i - 1].Left + (behind[i - 1].Scale * OldSchoolStack.CardWidth));
        }
    }

    [Fact]
    public void OldSchoolStack_HalfWayIsBetweenTheTwoPlaces()
    {
        var (scale, left, opacity) = OldSchoolStack.Place(0.5);

        Assert.Equal((OldSchoolStack.Place(0).Scale + OldSchoolStack.Place(1).Scale) / 2, scale, 6);
        Assert.Equal((OldSchoolStack.Place(0).Left + OldSchoolStack.Place(1).Left) / 2, left, 6);
        Assert.Equal(1, opacity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(5)]
    [InlineData(40)]
    public void OldSchoolStack_CardsPassedAndFarBehindAreOutOfSight(double depth) =>
        Assert.Equal(0, OldSchoolStack.Place(depth).Opacity);

    [Fact]
    public void OldSchoolStack_CardSlidingOutFadesToTheLeft()
    {
        var (scale, left, opacity) = OldSchoolStack.Place(-0.5);

        Assert.True(scale > 1);
        Assert.True(left < 0);
        Assert.InRange(opacity, 0.01, 0.99);
    }

    [Theory]
    [InlineData(0, 20, 0, 5)]
    [InlineData(7, 20, 6, 12)]
    [InlineData(19, 20, 18, 19)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(3, 2, 1, 1)]
    public void OldSchoolStack_HoldsArtForTheCardsOnScreen(int front, int count, int first, int last) =>
        Assert.Equal((first, last), OldSchoolStack.Window(front, count));

    [Theory]
    [InlineData(800, 1)]
    [InlineData(1183.4, 1)]
    [InlineData(1775.1, 1.5)]
    [InlineData(5000, 1.75)]
    [InlineData(double.PositiveInfinity, 1)]
    public void OldSchoolStack_GrowsToFillAWideScreenButNeverShrinks(double width, double fit) =>
        Assert.Equal(fit, OldSchoolStack.Fit(width), 3);

    [Theory]
    [InlineData(2.4, 0, 9, 2)]
    [InlineData(2.6, 0, 9, 3)]
    // A flick carries on a little way, and a slow drag does not.
    [InlineData(2.2, 4, 9, 3)]
    [InlineData(2.8, -4, 9, 2)]
    // Never past either end, however hard the flick.
    [InlineData(0.3, -40, 9, 0)]
    [InlineData(7.9, 40, 9, 8)]
    [InlineData(0, 0, 0, 0)]
    public void OldSchoolSwipe_SettlesOnTheNearestCardInTheWayItWasMoving(double position, double speed, int count, int card) =>
        Assert.Equal(card, OldSchoolSwipe.Rest(position, speed, count));

    [Theory]
    [InlineData(3.5, 9, 3.5)]
    [InlineData(-0.9, 9, -0.3)]
    [InlineData(8.6, 9, 8.2)]
    public void OldSchoolSwipe_FollowsTheFingerAndResistsPastTheEnds(double dragged, int count, double held) =>
        Assert.Equal(held, OldSchoolSwipe.Held(dragged, count), 6);

    [Fact]
    public void OldSchoolList_OpenCategoryIsFullSizeAndTheOnesAboveShrinkAndFade()
    {
        var places = Enumerable.Range(0, 6).Select(place => OldSchoolList.At(place)).ToList();

        Assert.Equal((1, 0, 1), places[0]);
        for (var i = 1; i < places.Count; i++)
        {
            Assert.True(places[i].Scale < places[i - 1].Scale);
            Assert.True(places[i].Up > places[i - 1].Up);
            Assert.True(places[i].Opacity < places[i - 1].Opacity);
        }

        // The one just left has gone, and so have those too far up the list to show.
        Assert.Equal(0, OldSchoolList.At(-1).Opacity);
        Assert.Equal(0, OldSchoolList.At(9).Opacity);
    }

    [Theory]
    // A long list goes round: the last is the one below the first, on its way out of sight.
    [InlineData(1, 0, 8, 1)]
    [InlineData(7, 0, 8, 7)]
    [InlineData(0, 0.5, 8, -0.5)]
    [InlineData(0, 8, 8, 0)]
    [InlineData(3, -1, 8, 4)]
    // A short one does not.
    [InlineData(3, 0, 4, 3)]
    [InlineData(0, 3, 4, -3)]
    public void OldSchoolList_PlacesCategoriesUpTheListFromTheOpenOne(int index, double position, int count, double place) =>
        Assert.Equal(place, OldSchoolList.Place(index, position, count), 6);

    [Theory]
    [InlineData(0, 1, 8, 1)]
    [InlineData(7, 1, 8, 0)]
    [InlineData(0, -1, 8, 7)]
    [InlineData(3, 1, 4, 3)]
    [InlineData(0, -1, 4, 0)]
    [InlineData(0, 1, 0, 0)]
    public void OldSchoolList_StepsRoundALongListAndStopsAtTheEndsOfAShortOne(int from, int step, int count, int to) =>
        Assert.Equal(to, OldSchoolList.Step(from, step, count));

    [Theory]
    [InlineData(0, 2, 8, 2)]
    [InlineData(0, 7, 8, -1)]
    [InlineData(6, 1, 8, 3)]
    [InlineData(0, 3, 4, 3)]
    [InlineData(3, 0, 4, -3)]
    public void OldSchoolList_TakesTheShorterWayRound(int from, int to, int count, int steps) =>
        Assert.Equal(steps, OldSchoolList.Steps(from, to, count));

    [Fact]
    public void OldSchoolStack_NoGamesNoArt()
    {
        var (first, last) = OldSchoolStack.Window(0, 0);

        Assert.True(last < first);
    }

    private void Touch(string name, string text = "")
    {
        File.WriteAllText(Path.Combine(_folder, name), text);
    }
}
