using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A game's build, as GOG's content system lists it for Windows.</summary>
internal sealed record GogBuild(string BuildId, string ProductId, string Version, string Link, DateTimeOffset? Published);

/// <summary>One depot of a build: a set of files, for a product (the game or a DLC) and some languages.</summary>
internal sealed record GogDepot(string Manifest, long Size, long CompressedSize, string ProductId, IReadOnlyList<string> Languages, bool IsGogDepot);

/// <summary>
/// A build's manifest: the depots that make it up, the folder it installs to, and the game's own client id and
/// secret, which its achievements are asked for with.
/// </summary>
internal sealed record GogBuildManifest(
    string BaseProductId,
    string BuildId,
    string InstallDirectory,
    IReadOnlyList<GogDepot> Depots,
    GogDepot? OfflineDepot,
    IReadOnlyList<string> Dependencies,
    string? ClientId,
    string? ClientSecret,
    bool ScriptInterpreter = false);

/// <summary>
/// GOG's content system, which GOG Galaxy installs from: the builds of a game, and each build's manifest. Its
/// manifests are JSON packed with zlib and need no sign-in; only the download addresses do. Read as gogdl
/// reads them, and checked with the sign-in probe on this Ally on 2026-11-14.
/// </summary>
internal static class GogContent
{
    private static HttpClient Http => GogAuth.Http;

    /// <summary>The game's newest public Windows build in GOG's current format, or null when it has none.</summary>
    public static async Task<GogBuild?> LatestBuildAsync(string productId, CancellationToken cancel = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://content-system.gog.com/products/{Uri.EscapeDataString(productId)}/os/windows/builds?generation=2", cancel);
            return ParseBuilds(json).FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask GOG for the builds of {productId}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    /// <summary>GOG's dependency repository: DOSBox, ScummVM and the runtime installers games name.</summary>
    public static async Task<List<GogDepots.Dependency>> DependenciesAsync(CancellationToken cancel = default)
    {
        try
        {
            var json = await Http.GetStringAsync("https://content-system.gog.com/dependencies/repository?generation=2", cancel);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("repository_manifest", out var url) && url.GetString() is { } link
                && await ZlibJsonAsync(link, "dependency list", cancel) is { } repository
                ? GogDepots.ParseDependencies(repository)
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not read GOG's dependency list: {ex.GetType().Name}");
            }

            return [];
        }
    }

    /// <summary>A build's manifest, from the link in its build entry.</summary>
    public static async Task<GogBuildManifest?> ManifestAsync(GogBuild build, CancellationToken cancel = default) =>
        await ZlibJsonAsync(build.Link, "build manifest", cancel) is { } json ? ParseManifest(json) : null;

    /// <summary>
    /// The public builds on the main branch in GOG's current format (generation 2), newest first. Builds on a
    /// branch are betas the player has to opt in to, and generation 1 builds are an older format.
    /// </summary>
    public static List<GogBuild> ParseBuilds(string json)
    {
        var builds = new List<GogBuild>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return builds;
            }

            foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var generation = item.TryGetProperty("generation", out var g) && g.TryGetInt32(out var n) ? n : 0;
                var onBranch = item.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String;
                var isPublic = !item.TryGetProperty("public", out var p) || p.ValueKind != JsonValueKind.False;
                if (generation != 2 || onBranch || !isPublic
                    || Str(item, "build_id") is not { } id || Str(item, "product_id") is not { } product || Str(item, "link") is not { } link)
                {
                    continue;
                }

                DateTimeOffset? published = Str(item, "date_published") is { } date && DateTimeOffset.TryParse(date, out var when) ? when : null;
                builds.Add(new GogBuild(id, product, Str(item, "version_name") ?? string.Empty, link, published));
            }
        }
        catch (JsonException)
        {
        }

        return builds.OrderByDescending(b => b.Published ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>A build manifest's JSON, unpacked.</summary>
    public static GogBuildManifest? ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "baseProductId") is not { } product || Str(root, "buildId") is not { } build)
            {
                return null;
            }

            var depots = root.TryGetProperty("depots", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(Depot).OfType<GogDepot>().ToList()
                : [];
            var offline = root.TryGetProperty("offlineDepot", out var o) ? Depot(o) : null;
            var dependencies = root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];

            return new GogBuildManifest(product, build, Str(root, "installDirectory") ?? product, depots, offline, dependencies,
                Str(root, "clientId"), Str(root, "clientSecret"),
                root.TryGetProperty("scriptInterpreter", out var script) && script.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GogDepot? Depot(JsonElement depot)
    {
        if (depot.ValueKind != JsonValueKind.Object || Str(depot, "manifest") is not { } manifest)
        {
            return null;
        }

        var languages = depot.TryGetProperty("languages", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : ["*"];
        return new GogDepot(manifest, Long(depot, "size"), Long(depot, "compressedSize"), Str(depot, "productId") ?? string.Empty, languages,
            depot.TryGetProperty("isGogDepot", out var gog) && gog.ValueKind == JsonValueKind.True);
    }

    /// <summary>Where GOG's content system keeps a manifest by its hash: two folders from the hash's start.</summary>
    public static string MetaUrl(string hash) =>
        $"https://cdn.gog.com/content-system/v2/meta/{hash[..2]}/{hash[2..4]}/{hash}";

    /// <summary>JSON packed with zlib, as GOG's manifests are; plain JSON is taken as it is.</summary>
    internal static async Task<string?> ZlibJsonAsync(string url, string what, CancellationToken cancel)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, cancel);
            return Unzlib(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not read a GOG {what}: {ex.GetType().Name}");
            }

            return null;
        }
    }

    /// <summary>The text of a zlib stream, or of the bytes as they are when they are not one.</summary>
    internal static string Unzlib(byte[] bytes)
    {
        // A zlib stream starts 0x78; JSON starts with a brace or white space.
        if (bytes is not [0x78, ..])
        {
            return Encoding.UTF8.GetString(bytes);
        }

        using var input = new MemoryStream(bytes);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
