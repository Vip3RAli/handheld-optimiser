using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A piece of a file: its packed and unpacked size and checksum. GOG keeps each one zlib packed by its packed checksum.</summary>
internal sealed record GogChunk(string Md5, long Size, string CompressedMd5, long CompressedSize);

/// <summary>A file of a depot, with the pieces it is made of in order.</summary>
/// <param name="Support">For the game's install script rather than the game: written under the game's support
/// folder, which the script copies from.</param>
/// <param name="Dependency">From GOG's dependencies (DOSBox, ScummVM), whose pieces are on an open CDN of their own.</param>
internal sealed record GogFile(string Path, IReadOnlyList<GogChunk> Chunks, bool Support, bool Dependency = false)
{
    public long Size => Chunks.Sum(c => c.Size);

    public long CompressedSize => Chunks.Sum(c => c.CompressedSize);

    /// <summary>The file's pieces as one value, to tell whether a new build changed it.</summary>
    public string Signature => string.Join(',', Chunks.Select(c => c.Md5));
}

/// <summary>One of the CDN addresses <c>secure_link</c> gives, which chunk addresses are made from.</summary>
internal sealed record GogEndpoint(string Name, string UrlFormat, IReadOnlyDictionary<string, string> Parameters, int Priority, bool FallbackOnly);

/// <summary>How a game is started, from its <c>goggame-&lt;id&gt;.info</c>.</summary>
internal sealed record GogPlayTask(string Path, string? Arguments, string? WorkingDirectory);

/// <summary>
/// The pure parts of installing a GOG game from its depots, as GOG Galaxy and gogdl do: which depots to take,
/// the files in a depot, the CDN addresses of the pieces, and how the installed game is started. The network
/// and the disk are <see cref="GogInstaller"/>'s.
/// </summary>
internal static class GogDepots
{
    /// <summary>
    /// A dependency in GOG's dependency repository. An internal one (DOSBox, ScummVM) is files that go in the
    /// game's folder. Any other is a runtime installer (Visual C++, DirectX), which needs administrator.
    /// </summary>
    internal sealed record Dependency(string Id, string Manifest, bool Internal, string? Executable);

    /// <summary>The dependencies in GOG's repository manifest.</summary>
    public static List<Dependency> ParseDependencies(string json)
    {
        var list = new List<Dependency>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("depots", out var depots) || depots.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var depot in depots.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object))
            {
                if (Str(depot, "dependencyId") is not { } id || Str(depot, "manifest") is not { } manifest)
                {
                    continue;
                }

                var executable = depot.TryGetProperty("executable", out var e) && e.ValueKind == JsonValueKind.Object ? Str(e, "path") : null;
                list.Add(new Dependency(id, manifest, depot.TryGetProperty("internal", out var i) && i.ValueKind == JsonValueKind.True, executable));
            }
        }
        catch (JsonException)
        {
        }

        return list;
    }

    /// <summary>
    /// The depots to install: the base game's, in the player's language when the game has it, in English
    /// otherwise, and those for every language. Add-ons (other product ids) are left out.
    /// </summary>
    public static List<GogDepot> Choose(GogBuildManifest manifest, string language)
    {
        var game = manifest.Depots.Where(d => d.ProductId == manifest.BaseProductId || d.ProductId.Length == 0).ToList();
        bool Has(string lang) => game.Any(d => d.Languages.Any(l => Same(l, lang)));
        var wanted = Has(language) ? language : Has("en-US") ? "en-US" : game.SelectMany(d => d.Languages).FirstOrDefault(l => l != "*") ?? "*";

        return game.Where(d => d.Languages.Any(l => l == "*" || Same(l, wanted))).ToList();
    }

    // en-US matches en and en-GB matches en-US: a game in English is fine whatever the region.
    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>The language to ask for: Windows' own, as GOG writes languages.</summary>
    public static string WindowsLanguage() => CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "en-US";

    /// <summary>The files in a depot manifest. Folders and links are left out: folders come with their files.</summary>
    public static List<GogFile> ParseDepot(string json, bool dependency = false) =>
        dependency ? ParseDepotFiles(json).Select(f => f with { Dependency = true }).ToList() : ParseDepotFiles(json);

    private static List<GogFile> ParseDepotFiles(string json)
    {
        var files = new List<GogFile>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("depot", out var depot) || !depot.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return files;
            }

            foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                if (Str(item, "type") != "DepotFile" || Str(item, "path") is not { } path || SafePath(path) is not { } safe)
                {
                    continue;
                }

                var chunks = item.TryGetProperty("chunks", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(Chunk).OfType<GogChunk>().ToList()
                    : [];
                var support = item.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.Array
                    && f.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "support");
                files.Add(new GogFile(safe, chunks, support));
            }
        }
        catch (JsonException)
        {
        }

        return files;
    }

    private static GogChunk? Chunk(JsonElement chunk) =>
        Str(chunk, "md5") is { } md5 && Str(chunk, "compressedMd5") is { } packed
            ? new GogChunk(md5, Long(chunk, "size"), packed, Long(chunk, "compressedSize"))
            : null;

    /// <summary>
    /// A depot's path made safe to put under the game's folder: GOG writes them with backslashes, and a path
    /// that climbs out of the folder or names a drive is refused.
    /// </summary>
    internal static string? SafePath(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.TrimEnd(' ', '.').Length == 0 || p.Contains(':')))
        {
            return null;
        }

        return string.Join('\\', parts);
    }

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="root"/>, or null if it escapes it.</summary>
    internal static string? SafeFullPath(string root, string relative)
    {
        if (SafePath(relative) is not { } safe)
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, safe));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The CDN addresses in a <c>secure_link</c> answer, the preferred first, fallbacks last.</summary>
    public static List<GogEndpoint> ParseSecureLink(string json)
    {
        var endpoints = new List<GogEndpoint>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            {
                return endpoints;
            }

            foreach (var url in urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.Object))
            {
                if (Str(url, "url_format") is not { } format || !url.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var parameters = p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString()! : x.Value.GetRawText());
                var priority = url.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var n) ? n : 0;
                var fallback = url.TryGetProperty("fallback_only", out var fb) && fb.ValueKind == JsonValueKind.True;
                endpoints.Add(new GogEndpoint(Str(url, "endpoint_name") ?? "cdn", format, parameters, priority, fallback));
            }
        }
        catch (JsonException)
        {
        }

        return endpoints.OrderBy(e => e.FallbackOnly).ThenByDescending(e => e.Priority).ToList();
    }

    /// <summary>Where a piece is on a CDN: the endpoint's address with the piece's place added to its path.</summary>
    public static string ChunkUrl(GogEndpoint endpoint, string compressedMd5)
    {
        var url = endpoint.UrlFormat;
        foreach (var (name, value) in endpoint.Parameters)
        {
            var filled = name == "path" ? $"{value}/{GalaxyPath(compressedMd5)}" : value;
            url = url.Replace("{" + name + "}", filled);
        }

        return url;
    }

    /// <summary>Where a dependency's piece is: GOG's dependency store needs no sign-in.</summary>
    public static string DependencyChunkUrl(string compressedMd5) =>
        $"https://gog-cdn-fastly.gog.com/content-system/v2/dependencies/store/{GalaxyPath(compressedMd5)}";

    /// <summary>Where a dependency's depot manifest is.</summary>
    public static string DependencyMetaUrl(string hash) =>
        $"https://gog-cdn-fastly.gog.com/content-system/v2/dependencies/meta/{GalaxyPath(hash)}";

    /// <summary>GOG's two folders from a hash's start: <c>ab/cd/abcd...</c>.</summary>
    public static string GalaxyPath(string hash) => $"{hash[..2]}/{hash[2..4]}/{hash}";

    /// <summary>The game's main play task in its <c>goggame-&lt;id&gt;.info</c>: the exe, relative to the game's folder.</summary>
    public static GogPlayTask? ParsePlayTask(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var files = tasks.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.Object && Str(t, "type") == "FileTask" && Str(t, "path") is not null)
                .ToList();
            var primary = files.FirstOrDefault(t => t.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True);
            var task = primary.ValueKind == JsonValueKind.Object ? primary : files.FirstOrDefault();
            if (task.ValueKind != JsonValueKind.Object || SafePath(Str(task, "path")!) is not { } path)
            {
                return null;
            }

            var working = Str(task, "workingDir") is { } dir ? SafePath(dir) : null;
            return new GogPlayTask(path, Str(task, "arguments"), working);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A folder name for the game: its install folder from the manifest, with anything Windows refuses taken out.</summary>
    public static string FolderName(string installDirectory)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var name = new string(installDirectory.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return name.Length > 0 ? name : "Game";
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
