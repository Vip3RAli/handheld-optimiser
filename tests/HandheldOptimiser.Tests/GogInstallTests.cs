using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Installing a GOG game from its depots: choosing the depots, reading their files and the CDN's addresses,
/// and the download itself, run against pieces made here in place of GOG's CDN. The layouts are those GOG
/// gave the sign-in probe on 2026-11-14, with the ids, names and tokens made up.
/// </summary>
public sealed class GogInstallTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"gog-install-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ----- A made-up CDN -----

    /// <summary>A file cut into pieces of the given sizes, each packed as GOG packs them, and the CDN that serves them.</summary>
    private static (GogFile File, byte[] Content, Dictionary<string, byte[]> Cdn) MakeFile(string path, params int[] pieces)
    {
        var content = new byte[pieces.Sum()];
        new Random(path.GetHashCode()).NextBytes(content);
        var cdn = new Dictionary<string, byte[]>();
        var chunks = new List<GogChunk>();
        var offset = 0;
        foreach (var size in pieces)
        {
            var part = content.AsSpan(offset, size).ToArray();
            offset += size;
            using var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(part);
            }

            var bytes = packed.ToArray();
            var packedMd5 = Convert.ToHexStringLower(MD5.HashData(bytes));
            cdn[packedMd5] = bytes;
            chunks.Add(new GogChunk(Convert.ToHexStringLower(MD5.HashData(part)), size, packedMd5, bytes.Length));
        }

        return (new GogFile(path, chunks, Support: false), content, cdn);
    }

    private static Func<GogFile, GogChunk, CancellationToken, Task<byte[]>> Serve(Dictionary<string, byte[]> cdn) =>
        (_, chunk, _) => Task.FromResult(cdn[chunk.CompressedMd5]);

    [Fact]
    public async Task Every_piece_is_fetched_checked_and_written_in_place()
    {
        var big = MakeFile(@"bin\game.exe", 4096, 4096, 100);
        var small = MakeFile("data.pak", 10);
        var empty = new GogFile(@"saves\.keep", [], Support: false);
        var cdn = big.Cdn.Concat(small.Cdn).ToDictionary();
        var plans = new[] { big.File, small.File, empty }.Select(f => new GogInstaller.Planned(f, Path.Combine(_folder, f.Path))).ToList();
        var state = new InstallState();
        long written = 0;

        await GogInstaller.DownloadAsync(plans, state, Serve(cdn), n => Interlocked.Add(ref written, n), CancellationToken.None);

        Assert.Equal(big.Content, File.ReadAllBytes(Path.Combine(_folder, "bin", "game.exe")));
        Assert.Equal(small.Content, File.ReadAllBytes(Path.Combine(_folder, "data.pak")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(_folder, "saves", ".keep")));
        Assert.Equal(4096 + 4096 + 100 + 10, written);
        Assert.Equal(3, state.DoneFiles.Count);
        Assert.Empty(state.Partial);
    }

    [Fact]
    public async Task A_stopped_download_carries_on_with_only_the_pieces_it_had_not_got()
    {
        var file = MakeFile("big.bin", 1000, 1000, 1000, 1000);
        var plans = new[] { new GogInstaller.Planned(file.File, Path.Combine(_folder, "big.bin")) };
        var state = new InstallState();

        // The CDN goes away after two pieces.
        var served = 0;
        using var stop = new CancellationTokenSource();
        Task<byte[]> Flaky(GogFile _, GogChunk chunk, CancellationToken token)
        {
            if (Interlocked.Increment(ref served) > 2)
            {
                throw new HttpRequestException("gone");
            }

            return Task.FromResult(file.Cdn[chunk.CompressedMd5]);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => GogInstaller.DownloadAsync(plans, state, Flaky, _ => { }, stop.Token));
        var gotFirst = state.Partial.GetValueOrDefault("big.bin")?.Count ?? 0;
        Assert.InRange(gotFirst, 0, 2);
        Assert.Equal(4000 - (gotFirst * 1000), GogInstaller.Remaining([file.File], state));

        var fetched = new List<string>();
        await GogInstaller.DownloadAsync(plans, state, (_, chunk, _) =>
        {
            lock (fetched)
            {
                fetched.Add(chunk.CompressedMd5);
            }

            return Task.FromResult(file.Cdn[chunk.CompressedMd5]);
        }, _ => { }, CancellationToken.None);

        Assert.Equal(4 - gotFirst, fetched.Count);
        Assert.Equal(file.Content, File.ReadAllBytes(Path.Combine(_folder, "big.bin")));
        Assert.Contains("big.bin", state.DoneFiles);
    }

    [Fact]
    public void A_piece_that_does_not_match_its_checksums_is_refused()
    {
        var file = MakeFile("a.bin", 64);
        var chunk = file.File.Chunks[0];
        var packed = file.Cdn[chunk.CompressedMd5];

        Assert.Equal(file.Content, GogInstaller.Unpack(packed, chunk));

        var broken = packed.ToArray();
        broken[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => GogInstaller.Unpack(broken, chunk));
        Assert.Throws<InvalidDataException>(() => GogInstaller.Unpack(packed, chunk with { Md5 = new string('0', 32) }));
    }

    [Fact]
    public async Task Repair_finds_only_the_files_that_do_not_match()
    {
        var good = MakeFile("good.bin", 500);
        var bad = MakeFile("bad.bin", 500, 500);
        var gone = MakeFile("gone.bin", 10);
        var plans = new[] { good.File, bad.File, gone.File }.Select(f => new GogInstaller.Planned(f, Path.Combine(_folder, f.Path))).ToList();
        await GogInstaller.DownloadAsync(plans, new InstallState(), Serve(good.Cdn.Concat(bad.Cdn).Concat(gone.Cdn).ToDictionary()), _ => { }, CancellationToken.None);

        var path = Path.Combine(_folder, "bad.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[700] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.Delete(Path.Combine(_folder, "gone.bin"));

        Assert.Equal(["bad.bin", "gone.bin"], GogInstaller.Broken(plans, CancellationToken.None).Select(f => f.Path).Order());
    }

    [Fact]
    public void An_update_fetches_changed_and_new_files_and_takes_away_dropped_ones()
    {
        var same = MakeFile("same.bin", 10).File;
        var changed = MakeFile("changed.bin", 10).File;
        var added = MakeFile("added.bin", 10).File;
        var installed = new Dictionary<string, string>
        {
            ["same.bin"] = same.Signature,
            ["changed.bin"] = "old",
            ["dropped.bin"] = "old",
        };

        List<GogFile> build = [same, changed, added];

        Assert.Equal(["added.bin", "changed.bin"], GogInstaller.Changed(build, installed).Select(f => f.Path).Order());
        Assert.Equal(["dropped.bin"], GogInstaller.Removed(build, installed));
    }

    // ----- What GOG says -----

    [Fact]
    public void A_depot_lists_its_files_with_their_pieces_and_leaves_out_folders_and_bad_paths()
    {
        const string json = """
            {"depot":{"items":[
              {"path":"AK.EXE","flags":["executable"],"chunks":[{"md5":"92284a450444e256b185dc3b7ba917d8","size":238962,"compressedMd5":"df137035c24b7688ed6b5e939f0ca2ce","compressedSize":233047}],"type":"DepotFile"},
              {"path":"app\\dosbox.conf","flags":["support"],"chunks":[{"md5":"e4f47b7ff23ef62954f82d3a6e128903","size":1001,"compressedMd5":"027d7439a872ddaf6c033ebc358b598c","compressedSize":584}],"type":"DepotFile"},
              {"path":"Data","type":"DepotDirectory"},
              {"path":"..\\..\\Windows\\evil.dll","chunks":[],"type":"DepotFile"},
              {"path":"Data\\big.pak","chunks":[
                {"md5":"a","size":10,"compressedMd5":"b","compressedSize":5},
                {"md5":"c","size":20,"compressedMd5":"d","compressedSize":6}],"type":"DepotFile"}
            ]}}
            """;

        var files = GogDepots.ParseDepot(json);

        Assert.Equal(["AK.EXE", @"app\dosbox.conf", @"Data\big.pak"], files.Select(f => f.Path));
        Assert.False(files[0].Support);
        Assert.True(files[1].Support);
        Assert.Equal(30, files[2].Size);
        Assert.Equal("a,c", files[2].Signature);
    }

    [Theory]
    [InlineData(@"bin\game.exe", @"bin\game.exe")]
    [InlineData("bin/game.exe", @"bin\game.exe")]
    [InlineData(@"\bin\\game.exe", @"bin\game.exe")]
    [InlineData(@"..\game.exe", null)]
    [InlineData(@"C:\Windows\game.exe", null)]
    [InlineData("", null)]
    public void Paths_stay_inside_the_games_folder(string path, string? safe)
    {
        Assert.Equal(safe, GogDepots.SafePath(path));
    }

    private static GogBuildManifest Manifest(params GogDepot[] depots) =>
        new("100", "1", "Old Dungeon", depots, null, [], null, null);

    private static GogDepot Depot(string manifest, string product, params string[] languages) =>
        new(manifest, 10, 5, product, languages, false);

    [Fact]
    public void The_base_games_depots_are_taken_in_the_players_language()
    {
        var manifest = Manifest(
            Depot("common", "100", "*"),
            Depot("english", "100", "en-US"),
            Depot("german", "100", "de-DE"),
            Depot("dlc", "200", "*"));

        Assert.Equal(["common", "german"], GogDepots.Choose(manifest, "de-DE").Select(d => d.Manifest));
        Assert.Equal(["common", "english"], GogDepots.Choose(manifest, "en-GB").Select(d => d.Manifest));
        Assert.Equal(["common", "english"], GogDepots.Choose(manifest, "ja-JP").Select(d => d.Manifest));
    }

    [Fact]
    public void GOGs_dependencies_say_which_are_files_for_the_games_folder()
    {
        const string json = """
            {"depots":[
              {"dependencyId":"DOSBox074_2CS","executable":{"arguments":"","path":""},"internal":true,"languages":["*"],"manifest":"81e078fc28fe2a1cea2fb45b057b6167","size":8868679},
              {"dependencyId":"MSVC2017_x64","executable":{"arguments":"/install /quiet /norestart","path":"__redist/MSVC2017_x64/VC_redist.x64.exe"},"internal":false,"manifest":"e710f0abc7bd095ac6ec89fba805b591"}
            ]}
            """;

        var dependencies = GogDepots.ParseDependencies(json);

        Assert.Equal(2, dependencies.Count);
        Assert.True(dependencies[0].Internal);
        Assert.Equal("81e078fc28fe2a1cea2fb45b057b6167", dependencies[0].Manifest);
        Assert.False(dependencies[1].Internal);
        Assert.Equal("__redist/MSVC2017_x64/VC_redist.x64.exe", dependencies[1].Executable);
        Assert.Equal("https://gog-cdn-fastly.gog.com/content-system/v2/dependencies/store/81/e0/81e078fc28fe2a1cea2fb45b057b6167",
            GogDepots.DependencyChunkUrl("81e078fc28fe2a1cea2fb45b057b6167"));
    }

    // Akalabeth's script as GOG gave it on 2026-11-14.
    private const string AkalabethScript = """
        {"actions":[
          {"install":{"action":"supportData","arguments":{"overwrite":true,"source":"{supportDir}/app","target":"{app}","type":"folder"}},"languages":["*"],"name":"DOSBoxConfig"},
          {"install":{"action":"setRegistry","arguments":{"root":"HKEY_LOCAL_MACHINE","subkey":"Software\\GOG.com\\Games\\{productID}","valueData":"{app}\\DOSBox","valueName":"DOSBOXFOLDER","valueType":"String"}},"languages":["*"],"name":"DOSBoxConfigLegacy1"},
          {"install":{"action":"supportData","arguments":{"target":"{app}/cloud_saves","type":"folder"}},"languages":["*"],"name":"DOSBoxCSFolder"}
        ],"productId":"1207666073"}
        """;

    [Fact]
    public void A_script_copies_its_support_files_into_place_makes_its_folders_and_leaves_the_registry()
    {
        var app = Path.Combine(_folder, "Akalabeth");
        var support = Path.Combine(app, GogInstaller.SupportFolder);
        Directory.CreateDirectory(Path.Combine(support, "app"));
        File.WriteAllText(Path.Combine(support, "app", "dosboxAkalabeth.conf"), "[autoexec]");

        var actions = GogScript.Parse(AkalabethScript);
        var left = GogScript.Run(actions, app, support, "1207666073");

        Assert.Equal(3, actions.Count);
        Assert.Equal("[autoexec]", File.ReadAllText(Path.Combine(app, "dosboxAkalabeth.conf")));
        Assert.True(Directory.Exists(Path.Combine(app, "cloud_saves")));
        Assert.Equal(["DOSBoxConfigLegacy1 (setRegistry)"], left);
    }

    [Fact]
    public void A_script_cannot_reach_outside_the_games_folder()
    {
        var app = Path.Combine(_folder, "Game");

        Assert.Null(GogScript.Resolve("{app}/../../Windows", app, Path.Combine(app, "__support"), "1"));
        Assert.Null(GogScript.Resolve("C:/Windows/System32", app, Path.Combine(app, "__support"), "1"));
        Assert.Null(GogScript.Resolve("{unknown}/x", app, Path.Combine(app, "__support"), "1"));
        Assert.Equal(Path.Combine(app, "saves"), GogScript.Resolve("{app}/saves", app, Path.Combine(app, "__support"), "1"));
    }

    [Fact]
    public void The_CDN_addresses_are_read_preferred_first_and_a_pieces_address_made_from_them()
    {
        const string json = """
            {"product_id":100,"type":"depot","urls":[
              {"endpoint_name":"fastly","url_format":"{base_url}/token=nva={expires_at}~dirs={dirs}~token={token}{path}",
               "parameters":{"base_url":"https://fastly.example","path":"/content-system/v2/store/100","token":"t2","expires_at":1794703268,"dirs":4},
               "priority":1,"max_fails":100,"supports_generation":[2],"fallback_only":true},
              {"endpoint_name":"gcore","url_format":"{base_url}/{path}?wsSecret={token}&wsTime={time}&prefix={prefix}",
               "parameters":{"base_url":"https://gcore.example","path":"content-system/v2/store/100","token":"t1","time":"6af8ffa4","prefix":"/content-system/v2/store/100/"},
               "priority":10,"max_fails":100,"supports_generation":[1,2],"fallback_only":false}
            ]}
            """;

        var endpoints = GogDepots.ParseSecureLink(json);

        Assert.Equal(["gcore", "fastly"], endpoints.Select(e => e.Name));
        Assert.Equal("https://gcore.example/content-system/v2/store/100/df/13/df137035c24b7688ed6b5e939f0ca2ce?wsSecret=t1&wsTime=6af8ffa4&prefix=/content-system/v2/store/100/",
            GogDepots.ChunkUrl(endpoints[0], "df137035c24b7688ed6b5e939f0ca2ce"));
        Assert.Equal("https://fastly.example/token=nva=1794703268~dirs=4~token=t2/content-system/v2/store/100/df/13/df137035c24b7688ed6b5e939f0ca2ce",
            GogDepots.ChunkUrl(endpoints[1], "df137035c24b7688ed6b5e939f0ca2ce"));
    }

    [Fact]
    public void The_game_is_started_from_its_primary_play_task()
    {
        const string info = """
            {"gameId":"100","name":"Old Dungeon","playTasks":[
              {"category":"tool","name":"Settings","path":"config.exe","type":"FileTask"},
              {"category":"game","isPrimary":true,"name":"Old Dungeon","path":"bin\\game.exe","arguments":"-windowed","workingDir":"bin","type":"FileTask"},
              {"category":"document","name":"Manual","link":"https://example.com","type":"URLTask"}
            ]}
            """;

        var task = GogDepots.ParsePlayTask(info);

        Assert.Equal(new GogPlayTask(@"bin\game.exe", "-windowed", "bin"), task);
        Assert.Equal("config.exe", GogDepots.ParsePlayTask("""{"playTasks":[{"path":"config.exe","type":"FileTask"}]}""")?.Path);
        Assert.Null(GogDepots.ParsePlayTask("""{"playTasks":[{"link":"https://example.com","type":"URLTask"}]}"""));
    }

    [Theory]
    [InlineData("Akalabeth - World of Doom", "Akalabeth - World of Doom")]
    [InlineData("Old: Dungeon?", "Old Dungeon")]
    [InlineData("Trailing...", "Trailing")]
    [InlineData("???", "Game")]
    public void A_games_folder_name_is_one_Windows_takes(string from, string name)
    {
        Assert.Equal(name, GogDepots.FolderName(from));
    }

    // ----- The library's record -----

    [Fact]
    public void The_record_lists_installed_games_with_the_key_the_store_gives_them()
    {
        var before = NativeInstalls.Folder;
        NativeInstalls.Folder = Path.Combine(_folder, "record");
        try
        {
            var game = Path.Combine(_folder, "Old Dungeon");
            Directory.CreateDirectory(Path.Combine(game, "bin"));
            File.WriteAllBytes(Path.Combine(game, "bin", "game.exe"), [0x4D, 0x5A]);

            NativeInstalls.Save(new NativeInstall(GameStore.Gog, "100", "Old Dungeon", "1", "1.0", game, @"bin\game.exe", "-windowed", null, 2, DateTimeOffset.UtcNow));
            NativeInstalls.Save(new NativeInstall(GameStore.Gog, "200", "Gone Game", "1", "1.0", Path.Combine(_folder, "nothing"), "game.exe", null, null, 2, DateTimeOffset.UtcNow));

            var listed = Assert.Single(NativeInstalls.Scan());
            Assert.Equal("gog:100", listed.Key);
            Assert.True(listed.Installed);
            Assert.Equal(Path.Combine(game, "bin", "game.exe"), listed.LaunchTarget);
            Assert.Equal(Path.Combine(game, "bin"), listed.WorkingDirectory);
            Assert.Equal("-windowed", listed.LaunchArguments);

            NativeInstalls.Remove("gog:100");
            Assert.Null(NativeInstalls.Find("gog:100"));
            Assert.NotNull(NativeInstalls.Find("gog:200"));
        }
        finally
        {
            NativeInstalls.Folder = before;
        }
    }

    [Theory]
    [InlineData(@"C:\Games", true)]
    [InlineData(@"C:\", false)]
    [InlineData("Games", false)]
    [InlineData(@"Q:\Games", false)]
    public void Only_a_full_path_on_a_drive_that_is_there_is_taken_as_the_install_folder(string folder, bool usable)
    {
        Assert.Equal(usable, OwnedGamesMenu.IsUsableFolder(folder));
    }
}
