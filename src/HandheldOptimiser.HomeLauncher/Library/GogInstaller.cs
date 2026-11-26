using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What an install is for: a new install, a newer build over an old one, or putting broken files right.</summary>
internal enum InstallMode
{
    Install,
    Update,
    Repair
}

/// <summary>
/// How far a download has got, kept on disk so it carries on where it stopped after a pause, sleep, a lost
/// connection or the library closing: the files finished, and the pieces finished of files part way.
/// </summary>
internal sealed class InstallState
{
    public string BuildId { get; set; } = string.Empty;

    public HashSet<string> DoneFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, HashSet<int>> Partial { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Installs a GOG game from its depots, as GOG Galaxy does, with no administrator and nothing of GOG's on the
/// device: the newest build's manifest, the depots for the player's language, each file's pieces from GOG's
/// CDN (zlib packed, each checked against both of its checksums), written straight into the game's folder.
/// Several pieces are fetched at once. Updates fetch only the files a new build changed; Repair fetches only
/// files whose pieces do not match. The pieces' layout is gogdl's, which Heroic uses.
/// </summary>
internal sealed class GogInstaller(HttpClient http)
{
    private const int Parallel = 6;

    /// <summary>
    /// The client pieces are fetched with. The library's shared client caps an answer at 16 MB, and some of
    /// Alwa's Awakening's pieces are bigger (seen 2026-11-14), so downloads have one of their own, with room
    /// for a few pieces at once and time for a big one on a slow connection.
    /// </summary>
    internal static readonly HttpClient Downloads = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = Parallel + 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    /// <summary>Where a game's support files go inside its folder, as GOG Galaxy keeps them.</summary>
    internal const string SupportFolder = "__support";
    internal const long MaxChunkBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(45);

    /// <summary>A file of the game with where it goes.</summary>
    internal sealed record Planned(GogFile File, string FullPath);

    // ----- Pure parts, tested -----

    /// <summary>
    /// A piece as GOG keeps it, unpacked and checked: the packed bytes against their checksum, the unpacked
    /// bytes against theirs and their size. A piece that fails either is refused, never written.
    /// </summary>
    public static byte[] Unpack(byte[] packed, GogChunk chunk)
    {
        if (chunk.Size is < 0 or > MaxChunkBytes)
        {
            throw new InvalidDataException("A piece was larger than allowed.");
        }

        if (!Md5Matches(packed, chunk.CompressedMd5))
        {
            throw new InvalidDataException("A piece did not match its packed checksum.");
        }

        var unpacked = new byte[chunk.Size];
        using (var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            var read = 0;
            int n;
            while (read < unpacked.Length && (n = zlib.Read(unpacked, read, unpacked.Length - read)) > 0)
            {
                read += n;
            }

            if (read != unpacked.Length)
            {
                throw new InvalidDataException("A piece was shorter than GOG said.");
            }
        }

        if (!Md5Matches(unpacked, chunk.Md5))
        {
            throw new InvalidDataException("A piece did not match its checksum.");
        }

        return unpacked;
    }

    internal static bool Md5Matches(ReadOnlySpan<byte> bytes, string md5) =>
        string.Equals(Convert.ToHexStringLower(MD5.HashData(bytes)), md5, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files an update has to fetch: new ones, and those whose pieces changed since the installed build.
    /// </summary>
    public static List<GogFile> Changed(IReadOnlyList<GogFile> files, IReadOnlyDictionary<string, string> installed) =>
        files.Where(f => !installed.TryGetValue(f.Path, out var signature) || signature != f.Signature).ToList();

    /// <summary>The files the installed build had that the new one does not, which an update deletes.</summary>
    public static List<string> Removed(IReadOnlyList<GogFile> files, IReadOnlyDictionary<string, string> installed)
    {
        var now = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.Keys.Where(p => !now.Contains(p)).ToList();
    }

    /// <summary>The bytes still to write: whole files not done, less the pieces already written of those part way.</summary>
    public static long Remaining(IEnumerable<GogFile> files, InstallState state) =>
        files.Where(f => !state.DoneFiles.Contains(f.Path))
            .Sum(f => f.Chunks.Where((_, i) => !(state.Partial.TryGetValue(f.Path, out var done) && done.Contains(i))).Sum(c => c.Size));

    /// <summary>
    /// Fetches and writes every piece not yet written, a few at once, keeping <paramref name="state"/> up to
    /// date as they land. A file is finished once all its pieces are in; a file of no pieces is written empty.
    /// </summary>
    /// <param name="fetch">Gets a piece's packed bytes.</param>
    /// <param name="written">Told the size of each piece as it is written.</param>
    public static async Task DownloadAsync(IReadOnlyList<Planned> files, InstallState state, Func<GogFile, GogChunk, CancellationToken, Task<byte[]>> fetch,
        Action<long> written, CancellationToken cancel)
    {
        // Lazy, so two pieces of one file landing together open it once.
        var handles = new ConcurrentDictionary<string, Lazy<SafeFileHandle>>(StringComparer.OrdinalIgnoreCase);
        var left = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var work = new List<(Planned Plan, int Index, long Offset)>();

        foreach (var plan in files.Where(p => !state.DoneFiles.Contains(p.File.Path)))
        {
            var done = state.Partial.GetValueOrDefault(plan.File.Path) ?? [];
            long offset = 0;
            var todo = 0;
            for (var i = 0; i < plan.File.Chunks.Count; i++)
            {
                if (!done.Contains(i))
                {
                    work.Add((plan, i, offset));
                    todo++;
                }

                offset += plan.File.Chunks[i].Size;
            }

            if (todo == 0)
            {
                // A file with no pieces, or with all of them in before a stop: written as it is.
                Directory.CreateDirectory(Path.GetDirectoryName(plan.FullPath)!);
                if (plan.File.Chunks.Count == 0)
                {
                    File.WriteAllBytes(plan.FullPath, []);
                }

                Finish(state, plan.File.Path);
                continue;
            }

            left[plan.File.Path] = todo;
        }

        try
        {
            await System.Threading.Tasks.Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancel },
                async (item, token) =>
                {
                    var chunk = item.Plan.File.Chunks[item.Index];
                    var bytes = Unpack(await fetch(item.Plan.File, chunk, token), chunk);

                    var handle = handles.GetOrAdd(item.Plan.File.Path, _ => new Lazy<SafeFileHandle>(() =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Plan.FullPath)!);
                        var h = File.OpenHandle(item.Plan.FullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                        if (RandomAccess.GetLength(h) != item.Plan.File.Size)
                        {
                            RandomAccess.SetLength(h, item.Plan.File.Size);
                        }

                        return h;
                    })).Value;
                    RandomAccess.Write(handle, bytes, item.Offset);

                    lock (state)
                    {
                        if (!state.Partial.TryGetValue(item.Plan.File.Path, out var done))
                        {
                            state.Partial[item.Plan.File.Path] = done = [];
                        }

                        done.Add(item.Index);
                    }

                    written(chunk.Size);
                    if (left.AddOrUpdate(item.Plan.File.Path, 0, (_, n) => n - 1) == 0 && handles.TryRemove(item.Plan.File.Path, out var finished))
                    {
                        finished.Value.Dispose();
                        lock (state)
                        {
                            Finish(state, item.Plan.File.Path);
                        }
                    }
                });
        }
        finally
        {
            foreach (var handle in handles.Values.Where(h => h.IsValueCreated))
            {
                handle.Value.Dispose();
            }
        }
    }

    private static void Finish(InstallState state, string path)
    {
        state.Partial.Remove(path);
        state.DoneFiles.Add(path);
    }

    /// <summary>The files of a folder whose pieces do not match the build's, which Repair fetches again.</summary>
    public static List<GogFile> Broken(IReadOnlyList<Planned> files, CancellationToken cancel)
    {
        var broken = new List<GogFile>();
        foreach (var plan in files)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(plan.FullPath) || new FileInfo(plan.FullPath).Length != plan.File.Size)
                {
                    broken.Add(plan.File);
                    continue;
                }

                using var stream = File.OpenRead(plan.FullPath);
                foreach (var chunk in plan.File.Chunks)
                {
                    var buffer = new byte[chunk.Size];
                    stream.ReadExactly(buffer);
                    if (!Md5Matches(buffer, chunk.Md5))
                    {
                        broken.Add(plan.File);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                broken.Add(plan.File);
            }
        }

        return broken;
    }

    // ----- Over the network -----

    /// <summary>Where each install keeps how far it has got.</summary>
    internal static string StatePath(string productId) => NativeInstalls.StatePath(GameStore.Gog, productId);

    /// <summary>The installed build's files and their pieces, which an update compares against.</summary>
    internal static string FilesPath(string productId) => NativeInstalls.FilesPath(GameStore.Gog, productId);

    /// <summary>
    /// Installs, updates or repairs a game. Stops with <see cref="OperationCanceledException"/> when paused
    /// or cancelled, having kept how far it got, and with <see cref="InstallException"/> when it cannot go on.
    /// </summary>
    public async Task<NativeInstall> RunAsync(InstallJob job, CancellationToken cancel)
    {
        // GOG not answering is not GOG saying no: the library starts before the Wi-Fi is up, and a download
        // picked up then was dropped, with how far it had got. Those are waited out and tried again.
        if (await GogAccountApi.FreshAsync(cancel) is not { AccessToken: { } })
        {
            throw StoreSessions.SignedIn(GameStore.Gog) ? Unreachable(cancel) : new InstallException("Sign in to GOG again, under Store Accounts.");
        }

        var builds = await GogContent.BuildsAsync(job.ProductId, cancel) ?? throw Unreachable(cancel);
        var build = builds.FirstOrDefault() ?? throw new InstallException("GOG has no Windows build of it to download.");
        var manifest = await GogContent.ManifestAsync(build, cancel) ?? throw Unreachable(cancel);

        // The game's files, from every depot it needs; a later depot's copy of a file wins. Support files go in
        // the game's support folder, where its script copies them from.
        var files = new Dictionary<string, GogFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var depot in GogDepots.Choose(manifest, GogDepots.WindowsLanguage()))
        {
            var json = await GogContent.ZlibJsonAsync(GogContent.MetaUrl(depot.Manifest), "depot", cancel) ?? throw Unreachable(cancel);
            foreach (var file in GogDepots.ParseDepot(json))
            {
                var placed = file.Support ? file with { Path = $@"{SupportFolder}\{file.Path}" } : file;
                files[placed.Path] = placed;
            }
        }

        // DOSBox, ScummVM and the like go in the game's folder. Runtime installers need administrator, and
        // the device has the common ones; each one left is logged.
        if (manifest.Dependencies.Count > 0)
        {
            var repository = await GogContent.DependenciesAsync(cancel);
            if (repository.Count == 0)
            {
                // Not read, rather than empty: going on would leave a game without its DOSBox.
                throw Unreachable(cancel);
            }

            foreach (var id in manifest.Dependencies)
            {
                if (repository.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) is not { Internal: true } dependency)
                {
                    Program.Log($"Not installing GOG's {id} for {job.ProductId}: a runtime installer, or not in GOG's list");
                    continue;
                }

                var json = await GogContent.ZlibJsonAsync(GogDepots.DependencyMetaUrl(dependency.Manifest), "dependency", cancel) ?? throw Unreachable(cancel);
                foreach (var file in GogDepots.ParseDepot(json, dependency: true).Where(f => !f.Support))
                {
                    files.TryAdd(file.Path, file);
                }
            }
        }

        if (files.Count == 0)
        {
            throw new InstallException("GOG listed no files for it.");
        }

        var installed = NativeInstalls.Find($"gog:{job.ProductId}");
        var folder = job.Folder ?? installed?.Folder ?? ChooseFolder(manifest);
        job.Folder = folder;
        var plans = files.Values.Select(f => new Planned(f, Path.Combine(folder, f.Path))).ToList();

        // What has to be fetched this time, and how far an earlier run got with it.
        var state = ReadState(job.ProductId);
        if (state.BuildId != build.BuildId)
        {
            state = new InstallState { BuildId = build.BuildId };
        }

        var oldFiles = ReadFiles(job.ProductId);
        var needed = job.Mode switch
        {
            InstallMode.Update => Changed(files.Values.ToList(), oldFiles),
            InstallMode.Repair => await Task.Run(() =>
            {
                job.Checking = true;
                try
                {
                    return Broken(plans, cancel);
                }
                finally
                {
                    job.Checking = false;
                }
            }, cancel),
            _ => files.Values.ToList(),
        };

        var neededPaths = needed.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var todo = plans.Where(p => neededPaths.Contains(p.File.Path)).ToList();
        job.Total = needed.Sum(f => f.Size);
        job.Done = job.Total - Remaining(needed, state);

        // Room for what is left, and a little over for Windows.
        var left = job.Total - job.Done;
        if (FreeSpace(folder) is { } free && free < left + (256L << 20))
        {
            throw new InstallException($"There is not enough room: {CleanResult.Size(left + (256L << 20) - free)} more is needed on {Path.GetPathRoot(folder)}.");
        }

        Directory.CreateDirectory(folder);
        WriteState(job.ProductId, state);

        var links = new SecureLinks(http, job.ProductId);
        using var saver = new Timer(_ =>
        {
            lock (state)
            {
                WriteState(job.ProductId, state);
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        try
        {
            await DownloadAsync(todo, state, (file, chunk, token) => links.FetchAsync(chunk, file.Dependency, token), size => Interlocked.Add(ref job.Done, size), cancel);
        }
        finally
        {
            await saver.DisposeAsync();
            lock (state)
            {
                WriteState(job.ProductId, state);
            }
        }

        // An update takes away the files the new build no longer has.
        if (job.Mode == InstallMode.Update)
        {
            foreach (var gone in Removed(files.Values.ToList(), oldFiles))
            {
                if (GogDepots.SafeFullPath(folder, gone) is { } target)
                {
                    TryDelete(target);
                }
            }
        }

        // The game's own install steps: settings copied into place, folders made.
        var script = Path.Combine(folder, $"goggame-{job.ProductId}.script");
        if (File.Exists(script))
        {
            var skipped = GogScript.Run(GogScript.Parse(await File.ReadAllTextAsync(script, cancel)), folder, Path.Combine(folder, SupportFolder), job.ProductId);
            if (skipped.Count > 0)
            {
                Program.Log($"Left out of {job.ProductId}'s install script: {string.Join(", ", skipped)}");
            }
        }

        var info = Path.Combine(folder, $"goggame-{job.ProductId}.info");
        var task = File.Exists(info) ? GogDepots.ParsePlayTask(await File.ReadAllTextAsync(info, cancel)) : null;
        if (task is null)
        {
            throw new InstallException("It installed, but GOG did not say how to start it.");
        }

        WriteFiles(job.ProductId, files.Values.ToDictionary(f => f.Path, f => f.Signature, StringComparer.OrdinalIgnoreCase));
        TryDelete(StatePath(job.ProductId));

        var record = new NativeInstall(GameStore.Gog, job.ProductId, job.Title, build.BuildId, build.Version, folder, task.Path, task.Arguments,
            task.WorkingDirectory, files.Values.Sum(f => f.Size), DateTimeOffset.UtcNow, job.Cover ?? installed?.Cover);
        NativeInstalls.Save(record);
        return record;
    }

    /// <summary>GOG did not answer, which the queue tries again in a while. A stop asked for meanwhile comes first.</summary>
    private static HttpRequestException Unreachable(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        return new HttpRequestException("GOG could not be reached.");
    }

    /// <summary>Where a new install goes: the install folder's GOG folder, under the game's own name.</summary>
    private static string ChooseFolder(GogBuildManifest manifest)
    {
        var root = Path.Combine(LibrarySettings.InstallFolder, "GOG");
        var name = GogDepots.FolderName(manifest.InstallDirectory);
        var folder = Path.Combine(root, name);

        // A folder of that name holding a game installed some other way is left alone. One holding only what
        // an uninstall kept (saves, settings) is this game's, and is installed into again; so is a half-finished one.
        return !Directory.Exists(folder) || File.Exists(StatePath(manifest.BaseProductId))
            || !Directory.EnumerateFiles(folder, "goggame-*.info").Any()
            ? folder
            : Path.Combine(root, $"{name} ({manifest.BaseProductId})");
    }

    /// <summary>Takes away a game the library installed. See <see cref="NativeInstalls.Uninstall"/>.</summary>
    public static bool Uninstall(NativeInstall install) => NativeInstalls.Uninstall(install);

    private static long? FreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static InstallState ReadState(string productId) => ReadJson<InstallState>(StatePath(productId)) ?? new InstallState();

    private static void WriteState(string productId, InstallState state) => WriteJson(StatePath(productId), state);

    internal static Dictionary<string, string> ReadFiles(string productId) =>
        ReadJson<Dictionary<string, string>>(FilesPath(productId)) is { } files
            ? new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static void WriteFiles(string productId, Dictionary<string, string> files) => WriteJson(FilesPath(productId), files);

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not keep how far a GOG download got: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The CDN addresses for a game's pieces, asked for with the account's token and asked for again before
    /// they run out or when the CDN refuses them. A piece that fails is tried on the next address.
    /// </summary>
    private sealed class SecureLinks(HttpClient http, string productId)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private List<GogEndpoint> _endpoints = [];
        private DateTime _got;

        /// <param name="dependency">A piece of DOSBox and the like, from GOG's open dependency store.</param>
        public async Task<byte[]> FetchAsync(GogChunk chunk, bool dependency, CancellationToken cancel)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                string url;
                if (dependency)
                {
                    url = GogDepots.DependencyChunkUrl(chunk.CompressedMd5);
                }
                else
                {
                    var endpoints = await EndpointsAsync(force: attempt > 0 && last is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }, cancel);
                    if (endpoints.Count == 0)
                    {
                        throw new InstallException("GOG did not give an address to download from.");
                    }

                    url = GogDepots.ChunkUrl(endpoints[attempt % endpoints.Count], chunk.CompressedMd5);
                }

                try
                {
                    using var answer = await http.GetAsync(url, cancel);
                    answer.EnsureSuccessStatusCode();
                    var bytes = await answer.Content.ReadAsByteArrayAsync(cancel);
                    if (Md5Matches(bytes, chunk.CompressedMd5))
                    {
                        return bytes;
                    }

                    last = new InvalidDataException("A piece came down broken.");
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                }
                catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
                {
                    // Timed out: tried again.
                    last = ex;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancel);
            }

            throw last ?? new InstallException("A piece could not be downloaded.");
        }

        private async Task<List<GogEndpoint>> EndpointsAsync(bool force, CancellationToken cancel)
        {
            if (!force && _endpoints.Count > 0 && DateTime.UtcNow - _got < LinkLifetime)
            {
                return _endpoints;
            }

            await _gate.WaitAsync(cancel);
            try
            {
                if (!force && _endpoints.Count > 0 && DateTime.UtcNow - _got < LinkLifetime)
                {
                    return _endpoints;
                }

                if (await GogAccountApi.FreshAsync(cancel) is not { AccessToken: { } access })
                {
                    throw StoreSessions.SignedIn(GameStore.Gog) ? Unreachable(cancel) : new InstallException("Sign in to GOG again, under Store Accounts.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://content-system.gog.com/products/{Uri.EscapeDataString(productId)}/secure_link?generation=2&path=/&_version=2");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using var answer = await http.SendAsync(request, cancel);
                if (!answer.IsSuccessStatusCode)
                {
                    Program.Log($"GOG answered {(int)answer.StatusCode} for the download addresses");
                    throw answer.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                        ? new InstallException("GOG says this account cannot download it.")
                        : Unreachable(cancel);
                }

                _endpoints = GogDepots.ParseSecureLink(await answer.Content.ReadAsStringAsync(cancel));
                _got = DateTime.UtcNow;
                return _endpoints;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
