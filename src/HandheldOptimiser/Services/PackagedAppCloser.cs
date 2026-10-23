using System.IO;
using System.Text.RegularExpressions;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>
/// A packaged app that is still running from its old version while Windows holds its update back.
/// </summary>
/// <param name="PackageFolder">The old version's folder under WindowsApps, which is also its package full name.</param>
/// <param name="ProcessCount">How many processes are running from that folder.</param>
/// <param name="Services">Windows services installed from that folder, which keep the package in use too.</param>
/// <param name="RunningServices">The ones running now, which are started again once the update is done.</param>
public sealed record OpenPackagedApp(
    string PackageFolder,
    int ProcessCount,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RunningServices);

/// <summary>
/// Closes a packaged app so that Windows can finish its update, for when the user asks. Windows will not
/// switch a packaged app (one under Program Files\WindowsApps) to a new version while anything from the old
/// one runs, and some apps keep running in the background after their window is closed: the Claude app
/// keeps a Windows service (CoworkVMService) as well as its tray processes. winget still reports success,
/// having only staged the new version next to the old one.
///
/// Only an app in exactly that state is offered: its old version's folder in use, and a newer folder with
/// the same name, architecture and publisher staged beside it. That is what the update leaves behind, and it
/// does not depend on the app's name in winget matching its package name.
/// </summary>
public sealed class PackagedAppCloser(LogService log, PowerShellRunner runner)
{
    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    /// <summary>A package folder: Name_Version_Architecture_ResourceId_PublisherId.</summary>
    private static readonly Regex PackageFolderShape = new(
        @"^(?<name>[A-Za-z0-9][A-Za-z0-9.\-]*)_(?<version>\d+\.\d+\.\d+\.\d+)_(?<arch>[A-Za-z0-9]+)_(?<res>[A-Za-z0-9.\-~]*)_(?<publisher>[a-z0-9]{13})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Service names go into a script, so only plain ones are used. Windows allows almost anything in a
    /// service name, but packaged services are named in the app's manifest and are plain in practice.
    /// </summary>
    private static readonly Regex ServiceNameShape = new(@"^[A-Za-z0-9_.\-]{1,256}$", RegexOptions.CultureInvariant);

    private static readonly Regex VersionShape = new(@"^\d+(\.\d+){0,3}$", RegexOptions.CultureInvariant);

    private static readonly string WindowsApps =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    /// <summary>
    /// Lists the processes and services running from any package folder. Each line is TYPE|fields, split by
    /// <see cref="Read"/>. A path cannot contain "|", so nothing needs escaping.
    /// </summary>
    private const string FindScript =
        """
        $ErrorActionPreference = 'SilentlyContinue'
        $apps = Join-Path $env:ProgramFiles 'WindowsApps'
        foreach ($p in Get-CimInstance Win32_Process) {
            if ($p.ExecutablePath -and $p.ExecutablePath.StartsWith("$apps\", [StringComparison]::OrdinalIgnoreCase)) {
                "PROC|$($p.ProcessId)|$($p.ExecutablePath)"
            }
        }
        foreach ($s in Get-CimInstance Win32_Service) {
            if ($s.PathName -and $s.PathName.IndexOf("$apps\", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                "SVC|$($s.Name)|$($s.State)|$($s.PathName)"
            }
        }
        """;

    /// <summary>
    /// Finds what is keeping <paramref name="item"/> on its old version, or null when it is not a packaged
    /// app waiting for that, or nothing of it is running any more.
    /// </summary>
    public async Task<OpenPackagedApp?> FindAsync(UpdateItem item, CancellationToken ct)
    {
        var outcome = await _runner.RunScriptAsync(
            FindScript,
            $"Look for what keeps {item.Name} open",
            ct,
            echoScript: false,
            logOutput: false,
            timeout: TimeSpan.FromMinutes(1));

        if (outcome.TimedOut || !outcome.Succeeded)
        {
            return null;
        }

        // WindowsApps cannot be listed without administrator rights, but one folder in it can be looked for.
        var app = Read(outcome.OutputLines, item, folder => Directory.Exists(Path.Combine(WindowsApps, folder)));
        if (app is not null)
        {
            _log.Trace($"    {item.Name}: {app.ProcessCount} process(es) and {app.Services.Count} service(s) running from {app.PackageFolder}");
        }

        return app;
    }

    /// <summary>
    /// Stops the app's services and ends its processes. Returns how many of its processes are still running
    /// afterwards, which is 0 when it worked.
    /// </summary>
    public async Task<int> CloseAsync(OpenPackagedApp app, CancellationToken ct)
    {
        if (!PackageFolderShape.IsMatch(app.PackageFolder))
        {
            return app.ProcessCount;
        }

        var services = string.Join(", ", app.Services.Where(s => ServiceNameShape.IsMatch(s)).Select(s => $"'{s}'"));

        // Services first, so none of them starts a process again once the processes are ended. The processes
        // are found again by folder here rather than by the IDs found earlier, which may since have been reused.
        var script =
            $$"""
            $folder = Join-Path (Join-Path $env:ProgramFiles 'WindowsApps') '{{app.PackageFolder}}'
            foreach ($name in @({{services}})) {
                try { Stop-Service -Name $name -Force -ErrorAction Stop; "Stopped the $name service." }
                catch { "Could not stop the $name service: $($_.Exception.Message)" }
            }
            function Get-Left {
                @(Get-CimInstance Win32_Process | Where-Object {
                    $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$folder\", [StringComparison]::OrdinalIgnoreCase)
                })
            }
            for ($i = 0; $i -lt 20; $i++) {
                $left = Get-Left
                if ($left.Count -eq 0) { break }
                foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
                Start-Sleep -Milliseconds 500
            }
            "LEFT|$((Get-Left).Count)"
            """;

        var outcome = await _runner.RunScriptAsync(script, $"Close {app.PackageFolder}", ct, echoScript: false);

        var left = outcome.OutputLines
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 2 && p[0] == "LEFT" && int.TryParse(p[1], out _))
            .Select(p => int.Parse(p[1]))
            .DefaultIfEmpty(app.ProcessCount)
            .Last();

        return left;
    }

    /// <summary>Starts again the services that were running before the app was closed.</summary>
    public async Task StartServicesAsync(OpenPackagedApp app, CancellationToken ct)
    {
        var names = app.RunningServices.Where(s => ServiceNameShape.IsMatch(s)).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var script =
            $$"""
            foreach ($name in @({{string.Join(", ", names.Select(s => $"'{s}'"))}})) {
                try { Start-Service -Name $name -ErrorAction Stop; "Started the $name service again." }
                catch { "Could not start the $name service: $($_.Exception.Message)" }
            }
            """;

        await _runner.RunScriptAsync(script, "Start the app's services again", ct, echoScript: false);
    }

    /// <summary>
    /// Reads what <see cref="FindScript"/> printed. Picks the package folder that things run from at
    /// <paramref name="item"/>'s installed version, and that has a folder of its available version staged
    /// beside it (asked of <paramref name="folderExists"/>), and counts what runs from it.
    /// </summary>
    public static OpenPackagedApp? Read(IEnumerable<string> lines, UpdateItem item, Func<string, bool> folderExists)
    {
        var installed = PackageVersion(item.InstalledVersion);
        var available = PackageVersion(item.AvailableVersion);
        if (installed is null || available is null || installed == available)
        {
            return null;
        }

        var rows = lines.Select(l => l.Split('|')).ToList();

        // The package folder a path is in: the part after \WindowsApps\, up to the next backslash.
        static string? FolderOf(string path)
        {
            const string marker = @"\WindowsApps\";
            var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : path[(at + marker.Length)..].Split('\\')[0];
        }

        var paths = rows
            .Where(p => p.Length == 3 && p[0] == "PROC").Select(p => p[2])
            .Concat(rows.Where(p => p.Length == 4 && p[0] == "SVC").Select(p => p[3]));

        var candidates = paths
            .Select(FolderOf)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => PackageFolderShape.Match(f))
            .Where(m => m.Success && m.Groups["version"].Value == installed)
            .Where(m => folderExists(
                $"{m.Groups["name"].Value}_{available}_{m.Groups["arch"].Value}_{m.Groups["res"].Value}_{m.Groups["publisher"].Value}"))
            .Select(m => m.Value)
            .ToList();

        // Two packages at the same versions would be a coincidence too far to guess between.
        if (candidates.Count != 1)
        {
            return null;
        }

        var folder = candidates[0];
        var inside = $@"\WindowsApps\{folder}\";

        var processes = rows.Count(p => p.Length == 3 && p[0] == "PROC" && p[2].Contains(inside, StringComparison.OrdinalIgnoreCase));

        var services = rows
            .Where(p => p.Length == 4 && p[0] == "SVC" && p[3].Contains(inside, StringComparison.OrdinalIgnoreCase)
                && ServiceNameShape.IsMatch(p[1]))
            .ToList();

        if (processes == 0 && services.All(s => !string.Equals(s[2], "Running", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new OpenPackagedApp(
            folder,
            processes,
            [.. services.Select(s => s[1])],
            [.. services.Where(s => string.Equals(s[2], "Running", StringComparison.OrdinalIgnoreCase)).Select(s => s[1])]);
    }

    /// <summary>
    /// A version as a package folder writes it, with four parts ("2.42304.2" becomes "2.42304.2.0"), or null
    /// when it is not plain numbers, as when winget says "Unknown" or cut the version short.
    /// </summary>
    public static string? PackageVersion(string? version)
    {
        if (version is null || !VersionShape.IsMatch(version))
        {
            return null;
        }

        var parts = version.Split('.').ToList();
        while (parts.Count < 4)
        {
            parts.Add("0");
        }

        return string.Join('.', parts);
    }
}
