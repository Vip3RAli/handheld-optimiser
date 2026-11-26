using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A build of an Epic game ready to download: its manifest, and the CDN addresses its pieces are under.</summary>
internal sealed record EpicBuild(EpicManifest Manifest, IReadOnlyList<string> BaseUrls);

/// <summary>How asking Epic for a build went, when it gave none.</summary>
internal enum EpicBuildProblem
{
    None,

    /// <summary>Epic could not be reached, or answered with a fault of its own: worth trying again.</summary>
    Unreachable,

    /// <summary>Epic says the account may not download it, or has no Windows build of it.</summary>
    Refused,

    /// <summary>The build's manifest is in a format the library does not read.</summary>
    Unreadable,
}

/// <summary>
/// Epic's launcher service, which the Epic Games Launcher installs from: asked with the account's token for
/// a game's live Windows build, it answers with where the build's manifest is on each of Epic's CDNs. The
/// pieces are under the same address as the manifest. Read as Legendary reads it.
/// </summary>
internal static class EpicContent
{
    private const string Assets = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/v2/platform/Windows";

    private static HttpClient Http => GogInstaller.Downloads;

    /// <summary>The address that answers with a game's live Windows build.</summary>
    internal static string AssetUrl(string ns, string catalogItemId, string appName) =>
        $"{Assets}/namespace/{Uri.EscapeDataString(ns)}/catalogItem/{Uri.EscapeDataString(catalogItemId)}/app/{Uri.EscapeDataString(appName)}/label/Live";

    /// <summary>The manifest addresses in the launcher service's answer, each with the query its CDN wants.</summary>
    internal static List<string> ParseAssets(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                return urls;
            }

            foreach (var element in elements.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                if (!element.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var manifest in manifests.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
                {
                    if (Str(manifest, "uri") is not { } uri || !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var query = manifest.TryGetProperty("queryParams", out var p) && p.ValueKind == JsonValueKind.Array
                        ? string.Join('&', p.EnumerateArray()
                            .Where(x => Str(x, "name") is not null && Str(x, "value") is not null)
                            .Select(x => $"{Str(x, "name")}={Str(x, "value")}"))
                        : string.Empty;
                    urls.Add(query.Length > 0 ? $"{uri}?{query}" : uri);
                }
            }
        }
        catch (JsonException)
        {
        }

        return urls;
    }

    /// <summary>The version of the build in the launcher service's answer.</summary>
    internal static string? ParseBuildVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("elements", out var elements)
                && elements.ValueKind == JsonValueKind.Array
                ? elements.EnumerateArray().Select(e => Str(e, "buildVersion")).FirstOrDefault(v => v is not null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version of a game's live Windows build, to tell whether an installed one is behind. Null when Epic could not be asked.</summary>
    public static async Task<string?> LatestVersionAsync(string accessToken, string ns, string catalogItemId, string appName, CancellationToken cancel = default) =>
        await EpicAuth.GetAsync(AssetUrl(ns, catalogItemId, appName), accessToken, "newest build", cancel) is { } json ? ParseBuildVersion(json) : null;

    /// <summary>The address a manifest's pieces are under: the manifest's own, without its file name or query.</summary>
    internal static string BaseUrl(string manifestUrl)
    {
        var path = manifestUrl.Split('?')[0];
        return path[..path.LastIndexOf('/')];
    }

    /// <summary>A game's live Windows build, or why there is none.</summary>
    public static async Task<(EpicBuild? Build, EpicBuildProblem Problem)> BuildAsync(string accessToken, string ns, string catalogItemId, string appName,
        CancellationToken cancel = default)
    {
        try
        {
            string json;
            using (var request = new HttpRequestMessage(HttpMethod.Get, AssetUrl(ns, catalogItemId, appName)))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", EpicAuth.Agent);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var answer = await Http.SendAsync(request, cancel);
                if (!answer.IsSuccessStatusCode)
                {
                    Program.Log($"Epic answered {(int)answer.StatusCode} for the build of {appName}");
                    return (null, answer.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound ? EpicBuildProblem.Refused : EpicBuildProblem.Unreachable);
                }

                json = await answer.Content.ReadAsStringAsync(cancel);
            }

            var urls = ParseAssets(json);
            if (urls.Count == 0)
            {
                return (null, EpicBuildProblem.Refused);
            }

            // Any CDN's copy of the manifest will do; the next is tried when one does not answer.
            foreach (var url in urls)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", EpicAuth.Agent);
                using var answer = await Http.SendAsync(request, cancel);
                if (!answer.IsSuccessStatusCode)
                {
                    Program.Log($"An Epic CDN answered {(int)answer.StatusCode} for the manifest of {appName}");
                    continue;
                }

                var bytes = await answer.Content.ReadAsByteArrayAsync(cancel);
                if (EpicManifests.Parse(bytes) is not { } manifest)
                {
                    Program.Log($"The manifest of {appName} is not one the library reads ({bytes.Length} bytes)");
                    return (null, EpicBuildProblem.Unreadable);
                }

                // The CDN that answered first, then the others.
                var bases = urls.Select(BaseUrl).Distinct().OrderByDescending(b => b == BaseUrl(url)).ToList();
                return (new EpicBuild(manifest, bases), EpicBuildProblem.None);
            }

            return (null, EpicBuildProblem.Unreachable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!cancel.IsCancellationRequested)
            {
                Program.Log($"Could not ask Epic for the build of {appName}: {ex.GetType().Name}");
            }

            return (null, EpicBuildProblem.Unreachable);
        }
    }

    /// <summary>A piece's bytes from the CDN, verified against its checksum. The next CDN is tried when one fails or sends bad data.</summary>
    public static async Task<byte[]> ChunkAsync(EpicBuild build, EpicChunk chunk, CancellationToken cancel)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var url = $"{build.BaseUrls[attempt % build.BaseUrls.Count]}/{EpicManifests.ChunkPath(chunk, build.Manifest.Version)}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", EpicAuth.Agent);
                using var answer = await Http.SendAsync(request, cancel);
                answer.EnsureSuccessStatusCode();
                var bytes = await answer.Content.ReadAsByteArrayAsync(cancel);
                _ = EpicManifests.UnpackChunk(bytes, chunk);
                return bytes;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                last = new InvalidDataException("A piece from Epic's CDN was invalid.", ex);
            }
            catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
            {
                // Timed out: tried again.
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancel);
        }

        throw last ?? new HttpRequestException("A piece could not be downloaded.");
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
