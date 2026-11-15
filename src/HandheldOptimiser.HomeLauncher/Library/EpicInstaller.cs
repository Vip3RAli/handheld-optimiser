using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// How far an Epic download has got, kept on disk so it carries on where it stopped: the pieces whose bytes
/// are all in the files they belong to.
/// </summary>
internal sealed class EpicInstallState
{
    public string BuildVersion { get; set; } = string.Empty;

    public HashSet<string> DoneChunks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Installs an Epic game from its build's pieces, as the Epic Games Launcher does, with no administrator and
/// without the launcher: each piece is fetched once from Epic's CDN, checked against the manifest's checksum,
/// and its bytes written to every place in every file that has them. Several pieces are fetched at once.
/// </summary>
internal static class EpicInstaller
{
    private const int Parallel = 8;

    /// <summary>Where some of a piece's bytes go: a place in a file.</summary>
    internal sealed record Use(string FullPath, long FileOffset, uint ChunkOffset, uint Size);

    /// <summary>A file of the build with where it goes.</summary>
    internal sealed record Planned(EpicFile File, string FullPath);

    /// <summary>
    /// Where each of a build's files goes under the game's folder. A file whose path would climb out of the
    /// folder is left out.
    /// </summary>
    public static List<Planned> Plan(IEnumerable<EpicFile> files, string folder) =>
        files.Select(f => GogDepots.SafePath(f.Path) is { } safe ? new Planned(f, Path.Combine(folder, safe)) : null).OfType<Planned>().ToList();

    /// <summary>Each piece the files need, with every place its bytes go.</summary>
    public static Dictionary<string, List<Use>> Uses(IEnumerable<Planned> files)
    {
        var uses = new Dictionary<string, List<Use>>(StringComparer.Ordinal);
        foreach (var plan in files)
        {
            long offset = 0;
            foreach (var part in plan.File.Parts)
            {
                if (!uses.TryGetValue(part.Guid, out var list))
                {
                    uses[part.Guid] = list = [];
                }

                list.Add(new Use(plan.FullPath, offset, part.Offset, part.Size));
                offset += part.Size;
            }
        }

        return uses;
    }

    /// <summary>The bytes still to write: those of the pieces not yet done.</summary>
    public static long Remaining(IReadOnlyDictionary<string, List<Use>> uses, EpicInstallState state) =>
        uses.Where(u => !state.DoneChunks.Contains(u.Key)).Sum(u => u.Value.Sum(x => (long)x.Size));

    /// <summary>
    /// Makes every file at its full size, then fetches each piece not yet done and writes its bytes where they
    /// go, keeping <paramref name="state"/> up to date as pieces land.
    /// </summary>
    /// <param name="fetch">Gets a piece as the CDN keeps it.</param>
    /// <param name="written">Told how many bytes each piece put into the files.</param>
    public static async Task DownloadAsync(IReadOnlyList<Planned> files, IReadOnlyDictionary<string, EpicChunk> chunks, EpicInstallState state,
        Func<EpicChunk, CancellationToken, Task<byte[]>> fetch, Action<long> written, CancellationToken cancel)
    {
        foreach (var plan in files)
        {
            cancel.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(plan.FullPath)!);
            using var handle = File.OpenHandle(plan.FullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (RandomAccess.GetLength(handle) != plan.File.Size)
            {
                RandomAccess.SetLength(handle, plan.File.Size);
            }
        }

        var uses = Uses(files);
        var work = uses.Where(u => !state.DoneChunks.Contains(u.Key)).ToList();
        await System.Threading.Tasks.Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancel },
            async (item, token) =>
            {
                if (!chunks.TryGetValue(item.Key, out var chunk))
                {
                    throw new InvalidDataException("A file names a piece the build does not list.");
                }

                var bytes = EpicManifests.UnpackChunk(await fetch(chunk, token), chunk);
                long wrote = 0;
                foreach (var use in item.Value)
                {
                    if (use.ChunkOffset + (long)use.Size > bytes.Length)
                    {
                        throw new InvalidDataException("A file asks for more of a piece than it has.");
                    }

                    // Each write opens the file for itself: pieces of one file land together, and a game has
                    // more files than are worth holding open.
                    using var handle = File.OpenHandle(use.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    RandomAccess.Write(handle, bytes.AsSpan((int)use.ChunkOffset, (int)use.Size), use.FileOffset);
                    wrote += use.Size;
                }

                lock (state)
                {
                    state.DoneChunks.Add(item.Key);
                }

                written(wrote);
            });
    }

    /// <summary>The files of a folder that do not match the build's checksums, which a check of the game's files fetches again.</summary>
    public static List<EpicFile> Broken(IReadOnlyList<Planned> files, CancellationToken cancel)
    {
        var broken = new List<EpicFile>();
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
                if (!string.Equals(Convert.ToHexStringLower(SHA1.HashData(stream)), plan.File.Sha, StringComparison.OrdinalIgnoreCase))
                {
                    broken.Add(plan.File);
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

    /// <summary>The files an update has to fetch: new ones, and those the new build changed.</summary>
    public static List<EpicFile> Changed(IReadOnlyList<EpicFile> files, IReadOnlyDictionary<string, string> installed) =>
        files.Where(f => !installed.TryGetValue(f.Path, out var sha) || !string.Equals(sha, f.Sha, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The files the installed build had that the new one does not, which an update deletes.</summary>
    public static List<string> Removed(IReadOnlyList<EpicFile> files, IReadOnlyDictionary<string, string> installed)
    {
        var now = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.Keys.Where(p => !now.Contains(p)).ToList();
    }

    /// <summary>
    /// Installs, updates or checks the files of a game. Stops with <see cref="OperationCanceledException"/>
    /// when paused or cancelled, having kept how far it got; with <see cref="InstallException"/> when it
    /// cannot go on; and with <see cref="HttpRequestException"/> when Epic could not be reached, which the
    /// queue tries again.
    /// </summary>
    public static async Task<NativeInstall> RunAsync(InstallJob job, CancellationToken cancel)
    {
        if (job.Namespace is not { } ns || job.CatalogItemId is not { } item)
        {
            throw new InstallException("It is not in your Epic games. List them again under Store Accounts.");
        }

        if (await EpicAccountApi.FreshAsync(cancel) is not { AccessToken: { } access })
        {
            throw StoreSessions.SignedIn(GameStore.Epic) ? Unreachable(cancel) : new InstallException("Sign in to Epic again, under Store Accounts.");
        }

        var (build, problem) = await EpicContent.BuildAsync(access, ns, item, job.ProductId, cancel);
        if (build is null)
        {
            throw problem switch
            {
                EpicBuildProblem.Refused => new InstallException("Epic has no Windows build of it for this account."),
                EpicBuildProblem.Unreadable => new InstallException("It is in an older format the library cannot install. Its menu can install it with the Epic launcher."),
                _ => Unreachable(cancel),
            };
        }

        var manifest = build.Manifest;
        if (manifest.Files.Count == 0)
        {
            throw new InstallException("Epic listed no files for it.");
        }

        var key = $"epic:{job.ProductId}";
        var installed = NativeInstalls.Find(key);
        var folder = job.Folder ?? installed?.Folder ?? ChooseFolder(job);
        job.Folder = folder;
        var plans = Plan(manifest.Files, folder);

        // How far an earlier run got with this build. A check of the files starts from nothing each time:
        // which files are broken is found again, so what was done for the last set says nothing of this one.
        var statePath = NativeInstalls.StatePath(GameStore.Epic, job.ProductId);
        var state = job.Mode == InstallMode.Repair ? null : NativeInstalls.ReadJson<EpicInstallState>(statePath);
        if (state is null || state.BuildVersion != manifest.BuildVersion)
        {
            state = new EpicInstallState { BuildVersion = manifest.BuildVersion };
        }

        var oldFiles = NativeInstalls.ReadFiles(GameStore.Epic, job.ProductId);
        var needed = job.Mode switch
        {
            InstallMode.Update => Changed(manifest.Files, oldFiles),
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
            _ => manifest.Files.ToList(),
        };

        var neededPaths = needed.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var todo = plans.Where(p => neededPaths.Contains(p.File.Path)).ToList();
        var uses = Uses(todo);
        Interlocked.Exchange(ref job.Total, todo.Sum(p => p.File.Size));
        Interlocked.Exchange(ref job.Done, job.Total - Remaining(uses, state));

        // Room for what is left, and a little over for Windows.
        var left = job.Total - job.Done;
        if (FreeSpace(folder) is { } free && free < left + (256L << 20))
        {
            throw new InstallException($"There is not enough room: {CleanResult.Size(left + (256L << 20) - free)} more is needed on {Path.GetPathRoot(folder)}.");
        }

        Directory.CreateDirectory(folder);
        NativeInstalls.WriteJson(statePath, state);

        // A big game has a hundred thousand pieces, so how far it has got is kept less often than GOG's.
        using var saver = new Timer(_ =>
        {
            lock (state)
            {
                NativeInstalls.WriteJson(statePath, state);
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        try
        {
            var chunks = manifest.Chunks.ToDictionary(c => c.Guid, StringComparer.Ordinal);
            await DownloadAsync(todo, chunks, state, (chunk, token) => EpicContent.ChunkAsync(build, chunk, token), size => Interlocked.Add(ref job.Done, size), cancel);
        }
        finally
        {
            await saver.DisposeAsync();
            lock (state)
            {
                NativeInstalls.WriteJson(statePath, state);
            }
        }

        // An update takes away the files the new build no longer has.
        if (job.Mode == InstallMode.Update)
        {
            foreach (var gone in Removed(manifest.Files, oldFiles))
            {
                if (GogDepots.SafePath(gone) is { } safe)
                {
                    NativeInstalls.TryDelete(Path.Combine(folder, safe));
                }
            }
        }

        if (GogDepots.SafePath(manifest.LaunchExe) is not { } exe)
        {
            throw new InstallException("It installed, but Epic did not say how to start it.");
        }

        // A runtime installer the game names (Visual C++, DirectX) needs administrator, and the device has
        // the common ones; it is left, and logged.
        if (manifest.PrerequisitePath.Length > 0)
        {
            Program.Log($"Not running {job.ProductId}'s prerequisite installer ({string.Join(", ", manifest.PrerequisiteIds)}): it needs administrator");
        }

        NativeInstalls.WriteJson(NativeInstalls.FilesPath(GameStore.Epic, job.ProductId),
            manifest.Files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(f => f.Path, f => f.Sha, StringComparer.OrdinalIgnoreCase));
        NativeInstalls.TryDelete(statePath);

        var record = new NativeInstall(GameStore.Epic, job.ProductId, job.Title, manifest.BuildVersion, manifest.BuildVersion, folder, exe,
            manifest.LaunchCommand.Length > 0 ? manifest.LaunchCommand : null, null, manifest.Files.Sum(f => f.Size), DateTimeOffset.UtcNow,
            job.Cover ?? installed?.Cover, null, ns, item);
        NativeInstalls.Save(record);
        return record;
    }

    /// <summary>Epic did not answer, which the queue tries again in a while. A stop asked for meanwhile comes first.</summary>
    private static HttpRequestException Unreachable(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        return new HttpRequestException("Epic could not be reached.");
    }

    /// <summary>Where a new install goes: the install folder's Epic folder, under the game's own name.</summary>
    private static string ChooseFolder(InstallJob job)
    {
        var root = Path.Combine(LibrarySettings.InstallFolder, "Epic");
        var name = GogDepots.FolderName(job.Title);
        var folder = Path.Combine(root, name);

        // A folder of that name holding a game the Epic launcher installed (it keeps a .egstore folder in
        // each) is left alone. One holding only what an uninstall kept is this game's; so is a half-finished one.
        return !Directory.Exists(folder) || File.Exists(NativeInstalls.StatePath(GameStore.Epic, job.ProductId))
            || !Directory.Exists(Path.Combine(folder, ".egstore"))
            ? folder
            : Path.Combine(root, $"{name} ({job.ProductId})");
    }

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
}
