using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The game library's pure logic: reading Steam's config format, naming and listing ROM files, and the
/// play time wording. Nothing here reads or writes the registry.
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

    private void Touch(string name, string text = "")
    {
        File.WriteAllText(Path.Combine(_folder, name), text);
    }
}
