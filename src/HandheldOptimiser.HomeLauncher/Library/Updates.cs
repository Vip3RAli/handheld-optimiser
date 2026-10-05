using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Tells the library when a newer release is on GitHub. It only looks: the library does not run as
/// administrator and never downloads or runs an installer. Updating hands over to the main app, which
/// checks the release's signature before installing anything (see UpdateService there).
/// </summary>
internal static class Updates
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Vip3RAli/handheld-optimiser/releases/latest";
    private const string MainAppName = "HandheldOptimiser.exe";

    // Makes the main app go straight to installing the update; see App.OnStartup there.
    private const string UpdateArgument = "--update";

    private static readonly Version Current = ReadOwnVersion();

    private static readonly HttpClient Http = CreateClient();

    /// <summary>"0.7.1" rather than the normalised "0.7.1.0".</summary>
    public static string Display(Version v) => v.ToString(v.Revision > 0 ? 4 : 3);

    /// <returns>The latest release's version if it is newer than this build, or null.</returns>
    public static async Task<Version?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestReleaseApi));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;

            if (!TryParseVersion(tag, out var latest) || latest <= Current)
            {
                return null;
            }

            Program.Log($"Handheld Optimiser {Display(latest)} is available");
            return latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            // Offline or rate-limited by GitHub is not worth interrupting anyone about.
            Program.Log($"Could not check for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens the main app to install the update. Windows asks for administrator permission, as it does
    /// whenever the main app starts.
    /// </summary>
    /// <returns>An error message, or null when the main app was started.</returns>
    public static string? StartUpdate()
    {
        var mainApp = Path.Combine(AppContext.BaseDirectory, MainAppName);
        if (!File.Exists(mainApp))
        {
            return "The update could not be started: Handheld Optimiser was not found beside the library.";
        }

        try
        {
            Process.Start(new ProcessStartInfo(mainApp)
            {
                Arguments = UpdateArgument,
                UseShellExecute = true
            })?.Dispose();

            Program.Log("Opened the main app to install the update");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Declining the administrator prompt lands here too.
            Program.Log($"Could not open the main app to update: {ex.Message}");
            return $"The update was not started: {ex.Message}";
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 1024 * 1024
        };

        // GitHub's API refuses requests with no user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HandheldOptimiser/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (Version.TryParse(text, out var parsed))
        {
            // Unset parts become zero, so "0.7.1" and "0.7.1.0" compare as the same release.
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    private static Version ReadOwnVersion()
    {
        var informational = typeof(Updates).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // "0.7.1+<commit>" when built from git; only the version part matters here.
        return TryParseVersion(informational.Split('+', '-')[0], out var version) ? version : new Version(0, 0, 0, 0);
    }
}
