using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Installing an Epic game from its build's pieces. The manifest and the pieces are made here in Epic's own
/// layout, as a real one read on this Ally on 2026-11-15 was laid out (Among Us, 106 files, 1,088 pieces),
/// with names and ids made up.
/// </summary>
public sealed class EpicInstallTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"epic-install-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ----- A build made in Epic's layout -----

    private sealed record Piece(uint[] Guid, ulong Hash, byte Group, byte[] Data)
    {
        public string Id => $"{Guid[0]:X8}{Guid[1]:X8}{Guid[2]:X8}{Guid[3]:X8}";
    }

    private static Piece MakePiece(uint seed, int size)
    {
        var data = new byte[size];
        new Random((int)seed).NextBytes(data);
        return new Piece([seed, seed + 1, 0xABCDEF00, 0x00000042], 0x1122334455667700UL + seed, (byte)(seed % 100), data);
    }

    private static void Text(BinaryWriter w, string text, bool wide = false)
    {
        if (text.Length == 0)
        {
            w.Write(0);
        }
        else if (wide)
        {
            w.Write(-(text.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(text));
            w.Write((ushort)0);
        }
        else
        {
            w.Write(text.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(text));
            w.Write((byte)0);
        }
    }

    private static byte[] Sized(Action<BinaryWriter> write)
    {
        using var inner = new MemoryStream();
        using (var w = new BinaryWriter(inner, Encoding.ASCII, leaveOpen: true))
        {
            write(w);
        }

        using var outer = new MemoryStream();
        using (var w = new BinaryWriter(outer))
        {
            w.Write((uint)(inner.Length + 4));
            w.Write(inner.ToArray());
        }

        return outer.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return packed.ToArray();
    }

    /// <param name="files">Each file's path, and its parts as (piece, offset in the piece, size).</param>
    private static byte[] MakeManifest(IReadOnlyList<Piece> pieces, IReadOnlyList<(string Path, (Piece Piece, uint Offset, uint Size)[] Parts)> files,
        int version = 21, string launchExe = "Game.exe", string launchCommand = "", bool wideName = false)
    {
        var meta = Sized(w =>
        {
            w.Write((byte)0);
            w.Write((uint)version);
            w.Write((byte)0);
            w.Write(0u);
            Text(w, "Lupin");
            Text(w, "1.2.3-build");
            Text(w, launchExe);
            Text(w, launchCommand);
            w.Write(1u);
            Text(w, "vcredist");
            Text(w, "Visual C++");
            Text(w, "Redist/vc.exe");
            Text(w, "/quiet");
        });

        var chunkList = Sized(w =>
        {
            w.Write((byte)0);
            w.Write((uint)pieces.Count);
            foreach (var p in pieces)
            {
                foreach (var part in p.Guid)
                {
                    w.Write(part);
                }
            }

            foreach (var p in pieces)
            {
                w.Write(p.Hash);
            }

            foreach (var p in pieces)
            {
                w.Write(SHA1.HashData(p.Data));
            }

            foreach (var p in pieces)
            {
                w.Write(p.Group);
            }

            foreach (var p in pieces)
            {
                w.Write((uint)p.Data.Length);
            }

            foreach (var p in pieces)
            {
                w.Write((long)(ChunkFile(p).Length));
            }
        });

        var fileList = Sized(w =>
        {
            w.Write((byte)0);
            w.Write((uint)files.Count);
            foreach (var f in files)
            {
                Text(w, f.Path, wideName);
            }

            foreach (var _ in files)
            {
                Text(w, string.Empty);
            }

            foreach (var f in files)
            {
                w.Write(SHA1.HashData(Content(f.Parts)));
            }

            foreach (var _ in files)
            {
                w.Write((byte)0);
            }

            foreach (var _ in files)
            {
                w.Write(0u);
            }

            foreach (var f in files)
            {
                w.Write((uint)f.Parts.Length);
                foreach (var (piece, offset, size) in f.Parts)
                {
                    w.Write(28u);
                    foreach (var part in piece.Guid)
                    {
                        w.Write(part);
                    }

                    w.Write(offset);
                    w.Write(size);
                }
            }
        });

        var body = meta.Concat(chunkList).Concat(fileList).ToArray();
        var packed = Zlib(body);
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(0x44BEC00Cu);
            w.Write(41u);
            w.Write((uint)body.Length);
            w.Write((uint)packed.Length);
            w.Write(SHA1.HashData(body));
            w.Write((byte)1);
            w.Write((uint)version);
            w.Write(packed);
        }

        return stream.ToArray();
    }

    private static byte[] Content((Piece Piece, uint Offset, uint Size)[] parts) =>
        parts.SelectMany(p => p.Piece.Data.AsSpan((int)p.Offset, (int)p.Size).ToArray()).ToArray();

    /// <summary>A piece as the CDN keeps it: Epic's header, then the bytes packed with zlib.</summary>
    private static byte[] ChunkFile(Piece piece)
    {
        var packed = Zlib(piece.Data);
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(0xB1FE3AA2u);
            w.Write(3u);
            w.Write(66u);
            w.Write((uint)packed.Length);
            foreach (var part in piece.Guid)
            {
                w.Write(part);
            }

            w.Write(piece.Hash);
            w.Write((byte)1);
            w.Write(SHA1.HashData(piece.Data));
            w.Write((byte)3);
            w.Write((uint)piece.Data.Length);
            w.Write(packed);
        }

        return stream.ToArray();
    }

    private static (EpicManifest Manifest, Dictionary<string, Piece> Pieces) Build()
    {
        var a = MakePiece(1, 4096);
        var b = MakePiece(2, 4096);
        var c = MakePiece(3, 1000);
        var bytes = MakeManifest([a, b, c],
        [
            ("Game.exe", [(a, 0, 4096), (b, 0, 1000)]),
            ("Data/level.dat", [(b, 1000, 3096), (c, 0, 1000)]),

            // The same bytes as part of the exe: one piece, two places.
            ("Data/copy.dat", [(a, 100, 900)]),
            ("Data/empty.txt", []),
        ]);
        return (EpicManifests.Parse(bytes)!, new[] { a, b, c }.ToDictionary(p => p.Id));
    }

    // ----- Reading -----

    [Fact]
    public void A_manifest_is_read_with_its_files_pieces_and_how_the_game_starts()
    {
        var (manifest, pieces) = Build();

        Assert.Equal(21, manifest.Version);
        Assert.Equal("Lupin", manifest.AppName);
        Assert.Equal("1.2.3-build", manifest.BuildVersion);
        Assert.Equal("Game.exe", manifest.LaunchExe);
        Assert.Equal(["vcredist"], manifest.PrerequisiteIds);
        Assert.Equal("Redist/vc.exe", manifest.PrerequisitePath);
        Assert.Equal("/quiet", manifest.PrerequisiteArguments);

        Assert.Equal(3, manifest.Chunks.Count);
        var first = manifest.Chunks[0];
        Assert.Equal("0000000100000002ABCDEF0000000042", first.Guid);
        Assert.Equal(0x1122334455667701UL, first.Hash);
        Assert.Equal(4096u, first.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(pieces[first.Guid].Data)), first.Sha);

        Assert.Equal(["Game.exe", "Data/level.dat", "Data/copy.dat", "Data/empty.txt"], manifest.Files.Select(f => f.Path));
        Assert.Equal(5096, manifest.Files[0].Size);
        Assert.Equal(new EpicChunkPart(manifest.Chunks[1].Guid, 1000, 3096), manifest.Files[1].Parts[0]);
        Assert.Empty(manifest.Files[3].Parts);
    }

    [Fact]
    public void A_name_in_two_byte_characters_is_read()
    {
        var piece = MakePiece(9, 64);

        var manifest = EpicManifests.Parse(MakeManifest([piece], [("Données/niveau.dat", [(piece, 0, 64)])], wideName: true));

        Assert.Equal("Données/niveau.dat", Assert.Single(manifest!.Files).Path);
    }

    [Fact]
    public void Bytes_that_are_not_a_manifest_or_were_changed_are_refused()
    {
        var piece = MakePiece(9, 64);
        var good = MakeManifest([piece], [("a.dat", [(piece, 0, 64)])]);
        var changed = (byte[])good.Clone();
        changed[^1] ^= 0xFF;

        Assert.NotNull(EpicManifests.Parse(good));
        Assert.Null(EpicManifests.Parse(changed));
        Assert.Null(EpicManifests.Parse(good[..30]));
        Assert.Null(EpicManifests.Parse(Encoding.UTF8.GetBytes("""{"ManifestFileVersion":"013000000000"}""")));
        Assert.Null(EpicManifests.Parse([]));
    }

    [Fact]
    public void A_pieces_address_has_its_folder_group_hash_and_id()
    {
        var chunk = new EpicChunk("0000000100000002ABCDEF0000000042", 0x00AB334455667701UL, "sha", 7, 1048576, 500);

        Assert.Equal("ChunksV4/07/00AB334455667701_0000000100000002ABCDEF0000000042.chunk", EpicManifests.ChunkPath(chunk, 21));
        Assert.Equal("ChunksV3", EpicManifests.ChunkFolder(12));
        Assert.Equal("ChunksV2", EpicManifests.ChunkFolder(4));
        Assert.Equal("Chunks", EpicManifests.ChunkFolder(2));
    }

    [Fact]
    public void A_piece_is_unpacked_and_one_that_does_not_match_is_refused()
    {
        var (manifest, pieces) = Build();
        var chunk = manifest.Chunks[0];
        var file = ChunkFile(pieces[chunk.Guid]);

        Assert.Equal(pieces[chunk.Guid].Data, EpicManifests.UnpackChunk(file, chunk));

        // Another piece's bytes under this piece's name.
        Assert.Throws<InvalidDataException>(() => EpicManifests.UnpackChunk(ChunkFile(pieces[manifest.Chunks[1].Guid]), chunk));
        Assert.Throws<InvalidDataException>(() => EpicManifests.UnpackChunk(new byte[80], chunk));
    }

    [Fact]
    public void The_launcher_services_answer_gives_each_CDNs_manifest_address_and_the_builds_version()
    {
        const string answer = """
            {"elements":[{"appName":"Lupin","labelName":"Live-Windows","buildVersion":"1.2.3-build","hash":"abc",
              "manifests":[
                {"uri":"https://cdn-one.example/Builds/Org/o-1/abc/default/Lupin.manifest","queryParams":[{"name":"token","value":"t1"},{"name":"exp","value":"99"}]},
                {"uri":"https://cdn-two.example/Builds/Org/o-1/abc/default/Lupin.manifest","queryParams":[]},
                {"uri":"http://plain.example/Lupin.manifest"}]}]}
            """;

        var urls = EpicContent.ParseAssets(answer);

        Assert.Equal(["https://cdn-one.example/Builds/Org/o-1/abc/default/Lupin.manifest?token=t1&exp=99",
            "https://cdn-two.example/Builds/Org/o-1/abc/default/Lupin.manifest"], urls);
        Assert.Equal("https://cdn-one.example/Builds/Org/o-1/abc/default", EpicContent.BaseUrl(urls[0]));
        Assert.Equal("1.2.3-build", EpicContent.ParseBuildVersion(answer));
        Assert.Empty(EpicContent.ParseAssets("""{"errorCode":"errors.com.epicgames.launcher.not_entitled"}"""));
        Assert.Null(EpicContent.ParseBuildVersion("not json"));
        Assert.Contains("/namespace/ns1/catalogItem/item1/app/Lupin/label/Live", EpicContent.AssetUrl("ns1", "item1", "Lupin"));
    }

    // ----- Downloading -----

    [Fact]
    public async Task A_game_is_put_together_from_its_pieces_each_fetched_once()
    {
        var (manifest, pieces) = Build();
        var plans = EpicInstaller.Plan(manifest.Files, _folder);
        var chunks = manifest.Chunks.ToDictionary(c => c.Guid);
        var state = new EpicInstallState();
        var fetched = new List<string>();
        long written = 0;

        await EpicInstaller.DownloadAsync(plans, chunks, state, (chunk, _) =>
        {
            lock (fetched)
            {
                fetched.Add(chunk.Guid);
            }

            return Task.FromResult(ChunkFile(pieces[chunk.Guid]));
        }, n => Interlocked.Add(ref written, n), CancellationToken.None);

        Assert.Equal(3, fetched.Count);
        Assert.Equal(3, fetched.Distinct().Count());
        Assert.Equal(manifest.Files.Sum(f => f.Size), written);
        Assert.Equal(3, state.DoneChunks.Count);
        Assert.Empty(EpicInstaller.Broken(plans, CancellationToken.None));
        Assert.Equal(0, new FileInfo(Path.Combine(_folder, "Data", "empty.txt")).Length);
        Assert.Equal(pieces[manifest.Chunks[0].Guid].Data.AsSpan(100, 900).ToArray(), File.ReadAllBytes(Path.Combine(_folder, "Data", "copy.dat")));
    }

    [Fact]
    public async Task A_download_that_stopped_carries_on_with_only_the_pieces_left()
    {
        var (manifest, pieces) = Build();
        var plans = EpicInstaller.Plan(manifest.Files, _folder);
        var chunks = manifest.Chunks.ToDictionary(c => c.Guid);
        var state = new EpicInstallState();
        var failing = manifest.Chunks[2].Guid;

        // The connection drops on one piece.
        await Assert.ThrowsAsync<IOException>(() => EpicInstaller.DownloadAsync(plans, chunks, state,
            (chunk, _) => chunk.Guid == failing ? throw new IOException("dropped") : Task.FromResult(ChunkFile(pieces[chunk.Guid])),
            _ => { }, CancellationToken.None));

        Assert.DoesNotContain(failing, state.DoneChunks);
        Assert.Single(EpicInstaller.Broken(plans, CancellationToken.None));
        Assert.Equal(1000, EpicInstaller.Remaining(EpicInstaller.Uses(plans), state));

        var fetched = new List<string>();
        await EpicInstaller.DownloadAsync(plans, chunks, state, (chunk, _) =>
        {
            lock (fetched)
            {
                fetched.Add(chunk.Guid);
            }

            return Task.FromResult(ChunkFile(pieces[chunk.Guid]));
        }, _ => { }, CancellationToken.None);

        Assert.All(fetched, guid => Assert.Equal(failing, guid));
        Assert.Empty(EpicInstaller.Broken(plans, CancellationToken.None));
    }

    [Fact]
    public async Task A_file_that_was_changed_or_deleted_is_found_by_a_check()
    {
        var (manifest, pieces) = Build();
        var plans = EpicInstaller.Plan(manifest.Files, _folder);
        await EpicInstaller.DownloadAsync(plans, manifest.Chunks.ToDictionary(c => c.Guid), new EpicInstallState(),
            (chunk, _) => Task.FromResult(ChunkFile(pieces[chunk.Guid])), _ => { }, CancellationToken.None);

        var exe = Path.Combine(_folder, "Game.exe");
        var bytes = File.ReadAllBytes(exe);
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(exe, bytes);
        File.Delete(Path.Combine(_folder, "Data", "level.dat"));

        Assert.Equal(["Game.exe", "Data/level.dat"], EpicInstaller.Broken(plans, CancellationToken.None).Select(f => f.Path));
    }

    [Fact]
    public void A_file_that_would_leave_the_games_folder_is_left_out()
    {
        var piece = MakePiece(9, 64);
        var manifest = EpicManifests.Parse(MakeManifest([piece], [("../../Windows/evil.dll", [(piece, 0, 64)]), ("ok.dat", [(piece, 0, 64)])]))!;

        var plan = Assert.Single(EpicInstaller.Plan(manifest.Files, _folder));

        Assert.Equal(Path.Combine(_folder, "ok.dat"), plan.FullPath);
    }

    [Fact]
    public void An_update_fetches_what_changed_and_deletes_what_went()
    {
        var (manifest, _) = Build();
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Game.exe"] = "0000000000000000000000000000000000000000",
            ["Data/level.dat"] = manifest.Files[1].Sha.ToUpperInvariant(),
            ["Data/old.dat"] = "1111111111111111111111111111111111111111",
        };

        Assert.Equal(["Game.exe", "Data/copy.dat", "Data/empty.txt"], EpicInstaller.Changed(manifest.Files, installed).Select(f => f.Path));
        Assert.Equal(["Data/old.dat"], EpicInstaller.Removed(manifest.Files, installed));
    }

    // ----- Starting -----

    [Fact]
    public void A_game_is_started_with_the_launchers_arguments()
    {
        var arguments = EpicLaunch.Arguments("c0de", "Lupin", "ns1", "0123456789abcdef", "Player_1");

        Assert.Equal("-AUTH_LOGIN=unused -AUTH_PASSWORD=c0de -AUTH_TYPE=exchangecode -epicapp=Lupin -epicenv=Prod -EpicPortal "
            + "-epicuserid=0123456789abcdef -epiclocale=en -epicusername=Player_1 -epicsandboxid=ns1", arguments);

        // A name that would split the command line is left out.
        Assert.DoesNotContain("epicusername", EpicLaunch.Arguments("c0de", "Lupin", null, "0123", "Player One\" -x"));
        Assert.DoesNotContain("epicsandboxid", EpicLaunch.Arguments("c0de", "Lupin", null, "0123", null));
    }

    [Fact]
    public void The_launch_code_is_read_from_Epics_answer()
    {
        Assert.Equal("c0de", EpicLaunch.ParseExchangeCode("""{"expiresInSeconds":300,"code":"c0de","creatingClientId":"34a0"}"""));
        Assert.Null(EpicLaunch.ParseExchangeCode("""{"errorCode":"errors.com.epicgames.common.oauth.invalid_token"}"""));
        Assert.Null(EpicLaunch.ParseExchangeCode("not json"));
    }

    [Fact]
    public void An_Epic_install_is_kept_with_its_ids_and_listed_as_installed()
    {
        var before = NativeInstalls.Folder;
        NativeInstalls.Folder = Path.Combine(_folder, "record");
        try
        {
            var game = Path.Combine(_folder, "Games", "Epic", "Old Dungeon");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "Game.exe"), "exe");
            NativeInstalls.Save(new NativeInstall(GameStore.Epic, "Lupin", "Old Dungeon", "1.2.3", "1.2.3", game, "Game.exe", "-windowed", null, 3,
                DateTimeOffset.UtcNow, "https://cdn.example/tall.jpg", null, "ns1", "item1"));

            var kept = NativeInstalls.Find("epic:Lupin");
            var listed = Assert.Single(NativeInstalls.Scan());

            Assert.Equal("ns1", kept!.Namespace);
            Assert.Equal("item1", kept.CatalogItemId);
            Assert.Equal("epic:Lupin", listed.Key);
            Assert.Equal(GameStore.Epic, listed.Store);
            Assert.Equal(Path.Combine(game, "Game.exe"), listed.LaunchTarget);
            Assert.Equal("-windowed", listed.LaunchArguments);
            Assert.Equal(NativeInstalls.StatePath(GameStore.Epic, "Lupin"), Path.Combine(NativeInstalls.Folder, "epic-Lupin.state.json"));

            // Uninstalled as GOG's are: the build's files go, the player's stay.
            File.WriteAllText(Path.Combine(game, "save.sav"), "mine");
            NativeInstalls.WriteJson(NativeInstalls.FilesPath(GameStore.Epic, "Lupin"), new Dictionary<string, string> { ["Game.exe"] = "sha" });
            Assert.True(NativeInstalls.Uninstall(kept));
            Assert.False(File.Exists(Path.Combine(game, "Game.exe")));
            Assert.True(File.Exists(Path.Combine(game, "save.sav")));
            Assert.Null(NativeInstalls.Find("epic:Lupin"));
        }
        finally
        {
            NativeInstalls.Folder = before;
        }
    }
}
