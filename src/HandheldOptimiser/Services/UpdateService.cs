using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandheldOptimiser.Services;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateOffer(
    Version Version,
    string Notes,
    Uri ReleasePage,
    string InstallerName,
    Uri InstallerUrl,
    long InstallerSize,
    Uri? SignatureUrl)
{
    /// <summary>False for a release published without a signature, which can only be installed by hand.</summary>
    public bool CanInstallInApp => SignatureUrl is not null;
}

/// <summary>Why a downloaded update was refused. Nothing has been run when this is thrown.</summary>
public sealed class UpdateRejectedException(string reason) : Exception(reason);

/// <summary>
/// Checks GitHub for a newer release and installs it.
///
/// This app runs as administrator, so whatever the updater runs, runs as administrator too. Nothing is
/// started unless its signed manifest (<see cref="UpdateManifest"/>) checks out against the public key
/// below, whose private half never leaves the developer's PC. That holds even if the GitHub account or
/// the download itself were tampered with. The installer is also downloaded into a folder only
/// administrators can write to, so it cannot be swapped between being checked and being run.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Vip3RAli/handheld-optimiser/releases/latest";
    private const string ReleasePagePrefix = "https://github.com/Vip3RAli/handheld-optimiser/releases/";

    /// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo). The private key is managed with tools\UpdateSigner.</summary>
    private const string SigningPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfPsMcAioOxJSTTJdSLlTIy4lZ2fTxcDJCAp4D9TKxO4UncxKx/SV8tNdxbqElR/UY3zXj62xGDbCnUkm33O2HQ==";

    private const long MaxApiResponseBytes = 1024 * 1024;
    private const long MaxSignatureBytes = 64 * 1024;
    private const long MaxInstallerBytes = 500L * 1024 * 1024;

    private const string DownloadFolderPrefix = "HandheldOptimiser-Update-";

    private static readonly Regex InstallerAssetName = new(
        @"^HandheldOptimiser-Setup-\d+(\.\d+){1,3}\.exe$", RegexOptions.CultureInvariant);

    private readonly LogService _log;
    private readonly HttpClient _http;

    public Version CurrentVersion { get; }

    /// <summary>"0.3.0" rather than the normalised "0.3.0.0".</summary>
    public static string Display(Version v) => v.ToString(v.Revision > 0 ? 4 : 3);

    public UpdateService(LogService log, Version? currentVersion = null)
    {
        _log = log;
        CurrentVersion = currentVersion is null ? ReadOwnVersion() : Normalise(currentVersion);

        // No overall timeout: installer downloads are large. Each call passes its own deadline instead.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"HandheldOptimiser/{CurrentVersion}");
    }

    /// <summary>Returns the latest release if it is newer than this build, or null.</summary>
    public async Task<UpdateOffer?> CheckAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        // GitHub answers 404 both when there is no release yet and when the repository is private.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new UpdateRejectedException("No public release of Handheld Optimiser was found on GitHub.");
        }

        response.EnsureSuccessStatusCode();

        var json = await ReadLimitedAsync(response, MaxApiResponseBytes, timeout.Token);
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;

        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var latest))
        {
            _log.Warning($"Latest release tag \"{tag}\" is not a version number; ignoring it.");
            return null;
        }

        if (latest <= CurrentVersion)
        {
            _log.Info($"Handheld Optimiser {Display(CurrentVersion)} is up to date.");
            return null;
        }

        var page = release.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        var releasePage = page is not null && page.StartsWith(ReleasePagePrefix, StringComparison.Ordinal)
            ? new Uri(page)
            : new Uri(ReleasePagePrefix + "latest");

        var assets = release.GetProperty("assets").EnumerateArray()
            .Select(a => (
                Name: a.GetProperty("name").GetString() ?? string.Empty,
                Url: a.GetProperty("browser_download_url").GetString() ?? string.Empty,
                Size: a.GetProperty("size").GetInt64()))
            .ToList();

        var installer = assets.FirstOrDefault(a => InstallerAssetName.IsMatch(a.Name));
        if (installer.Name is null || !IsHttps(installer.Url))
        {
            _log.Warning($"Release {Display(latest)} has no installer attached; ignoring it.");
            return null;
        }

        var signature = assets.FirstOrDefault(a => a.Name == installer.Name + ".sig");
        var signatureUrl = signature.Name is not null && IsHttps(signature.Url) ? new Uri(signature.Url) : null;

        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;

        _log.Info(signatureUrl is null
            ? $"Handheld Optimiser {Display(latest)} is available, but it is not signed for in-app updates."
            : $"Handheld Optimiser {Display(latest)} is available.");

        return new UpdateOffer(latest, notes, releasePage, installer.Name, new Uri(installer.Url), installer.Size, signatureUrl);
    }

    /// <summary>
    /// Downloads the offer's signed manifest and installer, and returns the installer's path only if
    /// every check passes. Throws <see cref="UpdateRejectedException"/> otherwise.
    /// </summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateOffer offer, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (offer.SignatureUrl is null)
        {
            throw new UpdateRejectedException("This release is not signed for in-app updates. Download it from the release page instead.");
        }

        progress?.Report("Checking the update's signature");
        var manifest = await DownloadManifestAsync(offer.SignatureUrl, ct);

        if (!VerifyManifest(manifest, SigningPublicKey, CurrentVersion, out var reason))
        {
            throw new UpdateRejectedException(reason!);
        }

        if (manifest.FileName != offer.InstallerName || manifest.Size != offer.InstallerSize ||
            !TryParseVersion(manifest.Version, out var signedVersion) || signedVersion != offer.Version)
        {
            throw new UpdateRejectedException("The signed details do not match the release on GitHub.");
        }

        _log.Success($"Signature verified for {manifest.FileName} ({manifest.Version}).");

        var folder = CreateAdminOnlyFolder();

        try
        {
            var installerPath = Path.Combine(folder, manifest.FileName);
            await DownloadVerifiedAsync(offer.InstallerUrl, installerPath, manifest, progress, ct);
            _log.Success($"Downloaded and verified {manifest.FileName}.");
            return installerPath;
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
    }

    /// <summary>
    /// Starts the verified installer silently. It closes this app, upgrades in place and, because of
    /// /UPDATE=1, reopens it afterwards (see installer\HandheldOptimiser.iss). The caller should exit.
    /// </summary>
    public void LaunchInstaller(string installerPath)
    {
        var folder = Path.GetDirectoryName(installerPath)!;

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = folder,
            UseShellExecute = false
        };

        foreach (var arg in new[]
                 {
                     "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS",
                     $"/LOG={Path.Combine(folder, "setup.log")}", "/UPDATE=1"
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        _log.Command($"Starting {Path.GetFileName(installerPath)} to install the update");
        Process.Start(psi)?.Dispose();
    }

    /// <summary>
    /// Removes download folders from earlier updates. Only folders this app could have made are touched:
    /// real folders (not links) owned by Administrators or SYSTEM, which nothing unelevated can create.
    /// </summary>
    public static void CleanUpOldDownloads(LogService log)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(programData, DownloadFolderPrefix + "*"))
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsOwnedByAdministrators(info))
                {
                    continue;
                }

                if (TryDeleteFolder(dir))
                {
                    log.Trace($"Removed the previous update download {dir}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Trace($"Could not tidy up old update downloads: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks a manifest's signature and contents. Separate from the download so the rules can be
    /// read, and tested, in one place.
    /// </summary>
    public static bool VerifyManifest(UpdateManifest manifest, string publicKeyBase64, Version currentVersion, out string? reason)
    {
        if (manifest.Product != UpdateManifest.ProductName || !manifest.FieldsAreWellFormed())
        {
            reason = "The update's signature file is malformed.";
            return false;
        }

        if (!InstallerAssetName.IsMatch(manifest.FileName) || !Regex.IsMatch(manifest.Sha256, "^[0-9a-fA-F]{64}$"))
        {
            reason = "The update's signature file names an unexpected file.";
            return false;
        }

        if (manifest.Size > MaxInstallerBytes)
        {
            reason = "The update is larger than any real installer would be.";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            reason = "The update's signature is malformed.";
            return false;
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            if (!key.VerifyData(manifest.SignedBytes(), signature, HashAlgorithmName.SHA256))
            {
                reason = "The update is not signed by the Handheld Optimiser developer.";
                return false;
            }
        }

        // Checked after the signature, and against the signed version: a genuine but older installer
        // must not be usable to roll this app back.
        if (!TryParseVersion(manifest.Version, out var version) || version <= Normalise(currentVersion))
        {
            reason = $"The update's version ({manifest.Version}) is not newer than this one ({Display(currentVersion)}).";
            return false;
        }

        reason = null;
        return true;
    }

    private async Task<UpdateManifest> DownloadManifestAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(await ReadLimitedAsync(response, MaxSignatureBytes, timeout.Token))
                   ?? throw new UpdateRejectedException("The update's signature file is empty.");
        }
        catch (JsonException)
        {
            throw new UpdateRejectedException("The update's signature file is malformed.");
        }
    }

    private async Task DownloadVerifiedAsync(
        Uri url, string path, UpdateManifest manifest, IProgress<string>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            var lastReportedMb = -1L;
            int read;

            while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > manifest.Size)
                {
                    throw new UpdateRejectedException("The download is larger than the signed installer.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);

                var mb = total / (1024 * 1024);
                if (mb != lastReportedMb)
                {
                    lastReportedMb = mb;
                    progress?.Report($"Downloading update: {mb} of {manifest.Size / (1024 * 1024)} MB");
                }
            }

            if (total != manifest.Size)
            {
                throw new UpdateRejectedException("The download is incomplete.");
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateRejectedException("The downloaded installer does not match its signature.");
            }
        }
    }

    /// <summary>
    /// A fresh, randomly named folder in %ProgramData% that only Administrators and SYSTEM can open,
    /// with that access set as it is created. ProgramData lets users create folders but not rename or
    /// delete other people's, so nothing unelevated can reach the installer once it is in here.
    /// </summary>
    private static string CreateAdminOnlyFolder()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (SafetyGuard.FindLinkOnPath(programData) is { } link)
        {
            throw new UpdateRejectedException($"{link} is a junction or symbolic link, so the update was not downloaded.");
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[] { administrators, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var folder = Path.Combine(programData, DownloadFolderPrefix + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(folder))
        {
            throw new UpdateRejectedException("The download folder already exists.");
        }

        security.CreateDirectory(folder);

        if (!IsOwnedByAdministrators(new DirectoryInfo(folder)))
        {
            throw new UpdateRejectedException("The download folder could not be secured.");
        }

        return folder;
    }

    private static bool IsOwnedByAdministrators(DirectoryInfo info)
    {
        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null &&
               (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use, usually by the installer that is running now. Removed on a later start.
            return false;
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new UpdateRejectedException("The response from GitHub was unexpectedly large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new UpdateRejectedException("The response from GitHub was unexpectedly large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (System.Version.TryParse(text, out var parsed))
        {
            version = Normalise(parsed);
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    /// <summary>
    /// Fills unset parts with zero. System.Version otherwise ranks "0.3.0" below "0.3.0.0", which would
    /// make the same release look newer than itself depending on how its number was written.
    /// </summary>
    private static Version Normalise(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version ReadOwnVersion()
    {
        var informational = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // "0.3.0+<commit>" when built from git; only the version part matters here.
        return TryParseVersion(informational.Split('+', '-')[0], out var version) ? version : new Version(0, 0, 0, 0);
    }
}
