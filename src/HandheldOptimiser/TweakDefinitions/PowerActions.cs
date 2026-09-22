using System.IO;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// One-click maintenance jobs. File deletion is limited to the folders listed in
/// <see cref="SafetyGuard"/>, and nothing here stops or reconfigures Windows Update.
/// </summary>
public static class PowerActions
{
    public static IReadOnlyList<PowerAction> All =>
    [
        FlushNetwork,
        ClearShaderCaches,
        DeepCleanup,
        CompactOs,
        UncompactOs
    ];

    /// <summary>
    /// Compresses the Windows install in place with the same mechanism OEMs use on small-storage devices.
    /// Files are decompressed on read, which a Zen 4 CPU does faster than the SSD can supply them, so load
    /// times do not suffer. Reversible at any time with <c>compact /CompactOS:never</c>.
    /// </summary>
    public static PowerAction CompactOs => new()
    {
        Id = "action.compact_os",
        Name = "Compact OS (save several GB)",
        Description =
            "Compresses the Windows system files to free space for games, typically 2 to 5 GB. Everything " +
            "keeps working as before; files are decompressed on the fly when read.",
        Glyph = "",
        CreateRestorePoint = true,
        DurationHint = "2 to 5 minutes",
        Warning =
            "Keep the Ally plugged in and do not close the app while this runs. It can be reversed later with " +
            "Undo Compact OS below.",
        Execute = async (ctx, progress, ct) =>
        {
            progress?.Report("Compressing Windows system files (this takes a few minutes)");

            var outcome = await ctx.Runner.RunProcessAsync(
                "compact.exe",
                ["/CompactOS:always"],
                "Compress the Windows installation",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("action.compact_os", "Windows system files are compressed.")
                : TweakResult.Fail("action.compact_os", $"compact.exe failed (exit {outcome.ExitCode}). See log.");
        }
    };

    /// <summary>
    /// Compact OS usually saves 2 to 5 GB, which decompressing gives back to Windows. The margin on top
    /// keeps the drive from being left completely full, which breaks updates and the page file.
    /// </summary>
    private const int UncompactMinFreeGb = 8;

    private static string? CheckSpaceForUncompact()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        try
        {
            var freeGb = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);

            return freeGb >= UncompactMinFreeGb
                ? null
                : $"Only {freeGb:0.0} GB is free on {root.TrimEnd('\\')}. Undo Compact OS needs at least " +
                  $"{UncompactMinFreeGb} GB, because the system files take up their full size again. Free up " +
                  "some space (uninstalling a game is the quickest way) and try again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not read the free space on {root.TrimEnd('\\')} ({ex.Message}).";
        }
    }

    /// <summary>
    /// The counterpart to <see cref="CompactOs"/>. Kept as its own button because compression state is
    /// not something System Restore or the undo journal can put back.
    /// </summary>
    public static PowerAction UncompactOs => new()
    {
        Id = "action.uncompact_os",
        Name = "Undo Compact OS",
        Description =
            "Decompresses the Windows system files again, returning them to how they were before Compact OS. " +
            "Does nothing if Windows is not compressed.",
        Glyph = "",
        CreateRestorePoint = true,
        DurationHint = "2 to 10 minutes",
        Warning =
            $"Needs at least {UncompactMinFreeGb} GB free on the system drive, since the files take up their " +
            "full size again. Keep the Ally plugged in and do not close the app while this runs.",
        Precheck = CheckSpaceForUncompact,
        Execute = async (ctx, progress, ct) =>
        {
            progress?.Report("Decompressing Windows system files (this takes a few minutes)");

            var outcome = await ctx.Runner.RunProcessAsync(
                "compact.exe",
                ["/CompactOS:never"],
                "Decompress the Windows installation",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("action.uncompact_os", "Windows system files are no longer compressed.")
                : TweakResult.Fail("action.uncompact_os", $"compact.exe failed (exit {outcome.ExitCode}). See log.");
        }
    };

    public static PowerAction FlushNetwork => new()
    {
        Id = "action.network_reset",
        Name = "Flush DNS & reset Winsock",
        Description =
            "Clears cached DNS lookups and resets the Winsock catalogue to defaults. The first thing to try " +
            "when a game cannot reach its servers or online play suddenly has high ping.",
        Glyph = "",
        RequiresReboot = true,
        CreateRestorePoint = true,
        DurationHint = "A few seconds",
        Warning =
            "Winsock reset removes third-party network filters. Some VPN clients need reinstalling or " +
            "repairing afterwards.",
        Execute = async (ctx, progress, ct) =>
        {
            progress?.Report("Flushing DNS cache");
            var dns = await ctx.Runner.RunProcessAsync("ipconfig.exe", ["/flushdns"], "Flush DNS resolver cache", ct);

            progress?.Report("Resetting Winsock catalogue");
            var winsock = await ctx.Runner.RunProcessAsync("netsh.exe", ["winsock", "reset"], "Reset Winsock catalogue", ct);

            return dns.Succeeded && winsock.Succeeded
                ? TweakResult.Ok("action.network_reset", "DNS flushed and Winsock reset.", rebootRequired: true)
                : TweakResult.Fail("action.network_reset", "DNS flush or Winsock reset reported an error. See log.");
        }
    };

    public static PowerAction ClearShaderCaches => new()
    {
        Id = "action.shader_cache",
        Name = "Clear shader caches",
        Description =
            "Deletes the DirectX and AMD (DX9/DX11/DX12/Vulkan/OpenGL/OpenCL) shader caches. Fixes stutter " +
            "or graphical corruption that appears after a driver update.",
        Glyph = "",
        DurationHint = "Under a minute",
        Warning =
            "Each game recompiles its shaders the next time it runs, so expect a few minutes of stutter on " +
            "first launch. Close games before running this, as caches in use are skipped.",
        Execute = (ctx, progress, ct) =>
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] caches =
            [
                Path.Combine(local, "D3DSCache"),
                Path.Combine(local, "AMD", "DxCache"),
                Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "DX9Cache"),
                Path.Combine(local, "AMD", "VkCache"),
                Path.Combine(local, "AMD", "OglCache"),
                Path.Combine(local, "AMD", "GLCache"),
                Path.Combine(local, "AMD", "cl.cache")
            ];

            return Task.Run(() => CleanAll("action.shader_cache", "Clear shader caches", caches, [], ctx, progress, ct), ct);
        }
    };

    /// <summary>
    /// Component cleanup is DISM's supported way to remove superseded update files from WinSxS. /ResetBase
    /// is deliberately not used: it would make every installed update permanent, and a bad cumulative
    /// update could then not be uninstalled.
    /// SoftwareDistribution\Download is left alone, since clearing it means stopping Windows Update.
    /// </summary>
    public static PowerAction DeepCleanup => new()
    {
        Id = "action.deep_cleanup",
        Name = "Force deep cleanup",
        Description =
            "Removes superseded Windows Update components with DISM, then deletes crash dumps and Windows " +
            "Error Reporting files. Installed updates can still be uninstalled afterwards.",
        Glyph = "",
        DurationHint = "5 to 15 minutes",
        Execute = async (ctx, progress, ct) =>
        {
            progress?.Report("Cleaning up superseded update components (this is the slow part)");
            var dism = await ctx.Runner.RunProcessAsync(
                "dism.exe",
                ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/NoRestart"],
                "Remove superseded Windows Update components",
                ct);

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            string[] folders =
            [
                Path.Combine(windows, "Minidump"),
                Path.Combine(windows, "LiveKernelReports"),
                Path.Combine(local, "CrashDumps"),
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue")
            ];

            string[] files = [Path.Combine(windows, "MEMORY.DMP")];

            var cleanup = await Task.Run(
                () => CleanAll("action.deep_cleanup", "Delete crash dumps and error reports", folders, files, ctx, progress, ct), ct);

            if (!dism.Succeeded)
            {
                return TweakResult.Fail("action.deep_cleanup",
                    $"DISM component cleanup failed (exit {dism.ExitCode}). Crash dump cleanup: {cleanup.Message}");
            }

            return cleanup.IsFailure
                ? cleanup
                : TweakResult.Ok("action.deep_cleanup", $"Component cleanup finished. {cleanup.Message}");
        }
    };

    private static TweakResult CleanAll(
        string id,
        string description,
        IEnumerable<string> folders,
        IEnumerable<string> files,
        TweakContext ctx,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ctx.Log.Command(description);

        var total = new CleanTally();

        foreach (var folder in folders)
        {
            progress?.Report($"Cleaning {Path.GetFileName(folder)}");
            total.Add(FileCleaner.CleanDirectoryContents(folder, ctx.Log, ct));
        }

        foreach (var file in files)
        {
            total.Add(FileCleaner.DeleteSingleFile(file, ctx.Log));
        }

        return total.Blocked
            ? TweakResult.Blocked(id, "A path was refused by the safety guard. See log.")
            : TweakResult.Ok(id, $"{total.Describe()}.");
    }
}
