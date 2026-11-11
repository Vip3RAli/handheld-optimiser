using System.IO;
using System.Windows.Media;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public class LibraryAdditionsTests
{
    private static Game DummyGame(string key = "steam:1091500", string title = "Cyberpunk 2077", GameStore store = GameStore.Steam) =>
        new(key, title, store, null, null, "steam.exe");

    [Fact]
    public void StoreAccounts_ParsesSteamLoginUsers()
    {
        var vdf = """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName"       "older_user"
                    "PersonaName"       "Older"
                    "RememberPassword"  "1"
                    "MostRecent"        "0"
                }
                "76561198000000002"
                {
                    "AccountName"       "viper_login"
                    "PersonaName"       "Viper"
                    "RememberPassword"  "1"
                    "MostRecent"        "1"
                }
            }
            """;

        var recent = StoreAccounts.ParseSteamLoginUsers(vdf);
        Assert.NotNull(recent);
        Assert.Equal("76561198000000002", recent.Value.SteamId);
        Assert.Equal("Viper", recent.Value.Name);

        var preferred = StoreAccounts.ParseSteamLoginUsers(vdf, "older_user");
        Assert.NotNull(preferred);
        Assert.Equal("76561198000000001", preferred.Value.SteamId);
        Assert.Equal("Older", preferred.Value.Name);
    }

    [Fact]
    public void StoreAccounts_ParsesEpicEaUbisoftAndBattleNetConfigs()
    {
        var epicIni = """
            [RememberMe]
            Enable=True
            Data=eyJhY2NvdW50IjoiMTIzIn0=
            LastUser=EpicPlayer
            UserId=epic_999
            """;
        var epic = StoreAccounts.ParseEpicUserSettings(epicIni);
        Assert.NotNull(epic);
        Assert.Equal("epic_999", epic.Value.UserId);
        Assert.Equal("EpicPlayer", epic.Value.Name);

        var heroicEpic = StoreAccounts.ParseHeroicEpicUser("""{"displayName": "HeroicPlayer", "account_id": "acc_42"}""");
        Assert.NotNull(heroicEpic);
        Assert.Equal("acc_42", heroicEpic.Value.UserId);
        Assert.Equal("HeroicPlayer", heroicEpic.Value.Name);

        var ea = StoreAccounts.ParseEaUserIni("user_100200300.ini", "user.displayname=EaPilot\n");
        Assert.NotNull(ea);
        Assert.Equal("100200300", ea.Value.UserId);
        Assert.Equal("EaPilot", ea.Value.Name);

        var ubi = StoreAccounts.ParseUbisoftAccount("username: \"UbiAgent\"\n", ["550e8400-e29b-41d4-a716-446655440000"]);
        Assert.NotNull(ubi);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", ubi.Value.UserId);
        Assert.Equal("UbiAgent", ubi.Value.Name);

        var bnet = StoreAccounts.ParseBattleNetConfig("""{"Client":{"SavedAccountNames":"player@example.com,second@example.com"}}""");
        Assert.Equal("player@example.com", bnet);
    }

    [Fact]
    public void StoreAccountStatus_DescribeFormatsCleanly()
    {
        Assert.Equal("Signed in (Viper)", new StoreAccountStatus(GameStore.Steam, "Steam", true, "Viper", "123", true).Describe());
        Assert.Equal("Signed in", new StoreAccountStatus(GameStore.Steam, "Steam", true, null, "123", true).Describe());
        Assert.Equal("Sign in", new StoreAccountStatus(GameStore.Epic, "Epic Games", false, null, null, true).Describe());
        Assert.Equal("Web sign in", new StoreAccountStatus(GameStore.Ubisoft, "Ubisoft Connect", false, null, null, false).Describe());
    }

    [Fact]
    public void Achievements_ParsesSteamWebApiAndCachesOnDisk()
    {
        var json = """
            {
                "playerstats": {
                    "steamID": "76561198000000002",
                    "gameName": "Hades II",
                    "achievements": [
                        {
                            "apiname": "ACH_FIRST_RUN",
                            "achieved": 1,
                            "unlocktime": 1730000000,
                            "name": "First Step",
                            "description": "Complete your first run."
                        },
                        {
                            "apiname": "ACH_BOSS_1",
                            "achieved": 0,
                            "unlocktime": 0,
                            "name": "Vanquisher",
                            "description": "Defeat the first guardian."
                        }
                    ]
                }
            }
            """;

        var parsed = Achievements.ParseSteamPlayerAchievements("steam:1145350", json);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.Unlocked);
        Assert.Equal(2, parsed.Total);
        Assert.Equal(50, parsed.Percentage);
        Assert.Equal("1 / 2 (50%)", parsed.Describe());

        var tempDir = Path.Combine(Path.GetTempPath(), "ho-ach-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Achievements.WriteCache(parsed, tempDir);
            var cached = Achievements.ReadCache("steam:1145350", tempDir);
            Assert.NotNull(cached);
            Assert.Equal(1, cached.Unlocked);
            Assert.Equal(2, cached.Total);
            Assert.Equal("First Step", cached.Items[0].Name);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void HeroicLibrary_ParsesLegendaryGogAndSideloadGames()
    {
        var legendaryJson = """
            {
                "Sugar": {
                    "app_name": "Sugar",
                    "title": "Rocket League",
                    "install_path": "C:\\Games\\RocketLeague",
                    "executable": "Binaries/Win64/RocketLeague.exe",
                    "is_dlc": false
                },
                "SugarDLC": {
                    "app_name": "SugarDLC",
                    "title": "Pack",
                    "install_path": "C:\\Games\\RocketLeague",
                    "is_dlc": true
                }
            }
            """;

        var epicGames = HeroicLibrary.ParseLegendaryInstalled(legendaryJson, checkDisk: false);
        Assert.Single(epicGames);
        Assert.Equal("epic:Sugar", epicGames[0].Key);
        Assert.Equal("Rocket League", epicGames[0].Title);
        Assert.Equal("heroic://launch/Sugar", epicGames[0].LaunchTarget);

        var gogInstalled = """{"installed": [{"appName": "1207658924", "install_path": "C:\\Games\\Witcher3"}]}""";
        var gogCache = """{"games": [{"app_name": "1207658924", "title": "The Witcher 3: Wild Hunt"}]}""";
        var gogGames = HeroicLibrary.ParseGogInstalled(gogInstalled, gogCache, checkDisk: false);
        Assert.Single(gogGames);
        Assert.Equal("gog:1207658924", gogGames[0].Key);
        Assert.Equal("The Witcher 3: Wild Hunt", gogGames[0].Title);

        var sideloadJson = """
            {
                "games": [
                    {
                        "app_name": "custom_1",
                        "title": "Indie Gem",
                        "install": { "executable": "C:\\Games\\Indie\\indie.exe" }
                    }
                ]
            }
            """;
        var sideload = HeroicLibrary.ParseSideloadLibrary(sideloadJson, checkDisk: false);
        Assert.Single(sideload);
        Assert.Equal("heroic:custom_1", sideload[0].Key);
        Assert.Equal("Indie Gem", sideload[0].Title);
    }

    [Fact]
    public void SaveGuard_CreatesRotatesAndRestoresSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ho-save-test-" + Guid.NewGuid().ToString("N"));
        var saveDir = Path.Combine(root, "saves-src");
        var backupsDir = Path.Combine(root, "backups");
        var restoreDir = Path.Combine(root, "restored");
        Directory.CreateDirectory(saveDir);

        try
        {
            var game = DummyGame("steam:1868140", "Dave the Diver");
            for (var i = 1; i <= 4; i++)
            {
                File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), $"save-state-{i}");
                var snap = SaveGuard.CreateSnapshot(game, saveDir, backupsDir);
                Assert.NotNull(snap);
                Thread.Sleep(15);
            }

            var snapshots = SaveGuard.SnapshotsFor(game, backupsDir);
            Assert.Equal(SaveGuard.MaxSnapshots, snapshots.Count);

            var restored = SaveGuard.RestoreSnapshot(snapshots[0], restoreDir);
            Assert.True(restored);
            Assert.Equal("save-state-4", File.ReadAllText(Path.Combine(restoreDir, "slot1.sav")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void A_restore_replaces_the_whole_save_folder_and_keeps_the_save_it_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "ho-save-test-" + Guid.NewGuid().ToString("N"));
        var saveDir = Path.Combine(root, "saves");
        var backupsDir = Path.Combine(root, "backups");
        Directory.CreateDirectory(saveDir);

        try
        {
            var game = DummyGame("gog:1", "Some Game");
            File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "old");
            var snapshot = SaveGuard.CreateSnapshot(game, saveDir, backupsDir)!;

            // Played on after the snapshot: the slot changed and a new file appeared.
            File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "new");
            File.WriteAllText(Path.Combine(saveDir, "slot2.sav"), "written later");

            Assert.True(SaveGuard.RestoreSnapshot(snapshot, saveDir));
            Assert.Equal("old", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
            Assert.False(File.Exists(Path.Combine(saveDir, "slot2.sav")));

            // The save that was replaced comes back with Undo last restore.
            var before = SaveGuard.BeforeRestoreFor(game, backupsDir);
            Assert.NotNull(before);
            Assert.Single(SaveGuard.SnapshotsFor(game, backupsDir));
            Assert.True(SaveGuard.RestoreSnapshot(before, saveDir));
            Assert.Equal("new", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
            Assert.Equal("written later", File.ReadAllText(Path.Combine(saveDir, "slot2.sav")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void A_broken_snapshot_leaves_the_save_as_it_is()
    {
        var root = Path.Combine(Path.GetTempPath(), "ho-save-test-" + Guid.NewGuid().ToString("N"));
        var saveDir = Path.Combine(root, "saves");
        Directory.CreateDirectory(saveDir);

        try
        {
            File.WriteAllText(Path.Combine(saveDir, "slot1.sav"), "keep me");
            var broken = Path.Combine(root, "save-broken.zip");
            File.WriteAllText(broken, "not a zip");

            Assert.False(SaveGuard.RestoreSnapshot(new SaveSnapshot(broken, saveDir, DateTimeOffset.UtcNow, 9), saveDir));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(saveDir, "slot1.sav")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Steam_stats_files_give_each_achievement_its_name_and_its_bit()
    {
        // Laid out as Steam writes them (checked against real files): achievements are the bits of a stat.
        static KvObject Bit(string name, string english, string? description = null)
        {
            var display = description is null
                ? Kv(("name", Kv(("english", english), ("german", "Deutsch"))))
                : Kv(("name", Kv(("english", english))), ("desc", Kv(("english", description))));
            return Kv(("name", name), ("display", display));
        }

        var bits = Kv(("0", Bit("ACH_WIN", "Winner", "Win a round.")), ("1", Bit("ACH_LOSE", "Loser")), ("31", Bit("ACH_TOP", "Top bit")));
        var stats = Kv(("1", Kv(("type", "ACHIEVEMENTS"), ("bits", bits))), ("2", Kv(("type", "INT"), ("name", "KILLS"))));
        var schema = Kv(("730", Kv(("stats", stats))));
        var user = Kv(("cache", Kv(("crc", 5), ("1", Kv(("data", unchecked((int)0x80000001)))))));

        var achievements = Achievements.ParseSteamStats("steam:730", "730", Bytes(schema), Bytes(user));

        Assert.NotNull(achievements);
        Assert.Equal(3, achievements.Total);
        Assert.Equal(2, achievements.Unlocked);
        var win = achievements.Items.Single(i => i.Id == "ACH_WIN");
        Assert.Equal("Winner", win.Name);
        Assert.Equal("Win a round.", win.Description);
        Assert.True(win.Unlocked);
        Assert.False(achievements.Items.Single(i => i.Id == "ACH_LOSE").Unlocked);
        Assert.True(achievements.Items.Single(i => i.Id == "ACH_TOP").Unlocked);

        // With no stats of the player's, every achievement is listed as locked.
        Assert.Equal(0, Achievements.ParseSteamStats("steam:730", "730", Bytes(schema), null)!.Unlocked);
    }

    [Fact]
    public void Steam_stats_that_are_not_KeyValues_give_nothing() =>
        Assert.Null(Achievements.ParseSteamStats("steam:730", "730", [0x00, 0x41, 0x42], null));

    // A binary KeyValues object: its entries, then the end mark. Texts are type 1, numbers type 2.
    private sealed record KvObject((string Name, object Value)[] Entries);

    private static KvObject Kv(params (string Name, object Value)[] entries) => new(entries);

    private static byte[] Bytes(KvObject root)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Write(writer, root);
        }

        return stream.ToArray();

        static void Write(BinaryWriter writer, KvObject kv)
        {
            foreach (var (name, value) in kv.Entries)
            {
                writer.Write(value switch { KvObject => (byte)0, string => (byte)1, _ => (byte)2 });
                writer.Write(System.Text.Encoding.UTF8.GetBytes(name + "\0"));
                switch (value)
                {
                    case KvObject inner:
                        Write(writer, inner);
                        break;
                    case string text:
                        writer.Write(System.Text.Encoding.UTF8.GetBytes(text + "\0"));
                        break;
                    case int number:
                        writer.Write(number);
                        break;
                }
            }

            writer.Write((byte)8);
        }
    }

    [Fact]
    public void GameTags_AndTileFilter_FilterAndListCollectionTabs()
    {
        var parsed = GameTags.Parse("Short Session; Docked Only ; short session ; Co-op");
        Assert.Equal(["Short Session", "Docked Only", "Co-op"], parsed);
        Assert.Equal("Short Session;Docked Only;Co-op", GameTags.Format(parsed));

        var tile1 = new GameTile
        {
            Game = DummyGame("steam:1145350", "Hades II"),
            Background = Brushes.Black,
            Tags = ["Short Session"]
        };
        var tile2 = new GameTile
        {
            Game = DummyGame("steam:1086940", "Baldur's Gate 3"),
            Background = Brushes.Black,
            Tags = ["Docked Only"]
        };

        var options = TileFilter.Options([tile1, tile2], storeTabs: true);
        Assert.Contains(TileFilter.ForTag("Short Session"), options);
        Assert.Contains(TileFilter.ForTag("Docked Only"), options);

        var shortSessionFilter = TileFilter.ForTag("Short Session");
        Assert.True(shortSessionFilter.Shows(tile1));
        Assert.False(shortSessionFilter.Shows(tile2));
    }

    [Fact]
    public void GameProfiles_ExportsAndImportsPresets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ho-preset-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = DummyGame("steam:2358720", "Black Myth: Wukong");
            var profile = new GameProfile(PowerMode.Performance, 60, 80, true, new Resolution(1920, 1080), 45, "quiet");

            var exportedPath = GameProfiles.ExportPreset(game, profile, tempDir);
            Assert.NotNull(exportedPath);
            Assert.True(File.Exists(exportedPath));

            var fromFile = GameProfiles.ImportPreset(exportedPath);
            Assert.Equal(profile, fromFile);

            var fromInline = GameProfiles.ImportPreset("power=performance;res=1920x1080;hz=60;brightness=80;closeapps=on;fps=45;fan=quiet");
            Assert.Equal(profile, fromInline);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("steam:2358720", "Black Myth: Wukong")]
    [InlineData("steam:990080", "Hogwarts Legacy")]
    [InlineData("steam:1593500", "God of War")]
    [InlineData("steam:1623730", "Palworld")]
    [InlineData("steam:582010", "Monster Hunter: World")]
    [InlineData("steam:2215430", "Ghost of Tsushima")]
    [InlineData("steam:1868140", "Dave the Diver")]
    [InlineData("steam:1145350", "Hades II")]
    [InlineData("steam:2161700", "Persona 3 Reload")]
    [InlineData("steam:2440510", "Forza Motorsport")]
    public void GameSettingsDb_IncludesAllNewExpandedTitles(string key, string title)
    {
        var db = GameSettingsDb.ReadBuiltIn();
        Assert.NotNull(db);

        var game = DummyGame(key, title);
        var rec = db.For(game);
        Assert.NotNull(rec);
        Assert.Equal(title, rec.Title);
        Assert.NotEmpty(rec.Settings);
    }
}
