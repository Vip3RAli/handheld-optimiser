using System.Text.RegularExpressions;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Where a Steam install has got to, as its tile says it.</summary>
internal enum InstallPhase
{
    /// <summary>In Steam's download queue, not started.</summary>
    Waiting,
    Downloading,
    Paused,

    /// <summary>Downloaded, and being put in place.</summary>
    Installing,
    Installed
}

/// <summary>
/// How far a Steam game's install has got, read from its <c>appmanifest_&lt;app&gt;.acf</c>. Steam writes
/// that file as soon as an install is confirmed in its dialog, with the sizes to download and to write, and
/// again when the download pauses, stops or finishes. It does not write the counts while it downloads: seen
/// on the Ally on 2026-11-14, when House Flipper's file still said 0 bytes with 1.3 GB downloaded. So while
/// Steam is at work, <see cref="Live"/> adds what Steam has written to disk since the file was last written.
/// </summary>
internal readonly partial record struct SteamInstallProgress(InstallPhase Phase, long Done, long Total)
{
    // StateFlags bits, as Steam's own EAppState names them.
    private const int FullyInstalled = 4;
    private const int UpdateRunning = 256;
    private const int UpdatePaused = 512;
    private const int UpdateStarted = 1024;
    private const int Downloading = 1 << 20;
    private const int Staging = 1 << 21;
    private const int Committing = 1 << 22;

    /// <summary>From 0 to 1, or 0 while Steam does not know the size yet.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;

    /// <summary>Whole per cent, rounded down so 100 only shows once it is true.</summary>
    public int Percent => (int)Math.Floor(Fraction * 100);

    /// <summary>What the tile says in place of its name.</summary>
    public string Text => Phase switch
    {
        InstallPhase.Waiting => "Waiting to download",
        InstallPhase.Downloading => Total > 0 ? $"Downloading {Percent}%" : "Downloading",
        InstallPhase.Paused => Total > 0 ? $"Paused at {Percent}%" : "Paused",
        InstallPhase.Installing => Total > 0 ? $"Installing {Percent}%" : "Installing",
        _ => "Installed"
    };

    /// <summary>The app id an <c>appmanifest_&lt;app&gt;.acf</c> file name is for, or null.</summary>
    public static string? AppIdFromFileName(string fileName)
    {
        var m = ManifestName().Match(fileName);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Reads a manifest's text. Null when it is not a manifest Steam has finished writing.</summary>
    public static SteamInstallProgress? Read(string acf)
    {
        if (Number(acf, "StateFlags") is not { } flags)
        {
            return null;
        }

        // An installed game with an update waiting still plays, so for a tile it is installed.
        if ((flags & FullyInstalled) != 0)
        {
            return new(InstallPhase.Installed, 0, 0);
        }

        var downloaded = Number(acf, "BytesDownloaded") ?? 0;
        var toDownload = Number(acf, "BytesToDownload") ?? 0;
        var staged = Number(acf, "BytesStaged") ?? 0;
        var toStage = Number(acf, "BytesToStage") ?? 0;
        var downloadLeft = toDownload == 0 || downloaded < toDownload;

        if ((flags & UpdatePaused) != 0)
        {
            return new(InstallPhase.Paused, downloaded, toDownload);
        }

        // Staging and committing come after the download: the files are written to the game's folder.
        if ((flags & (Staging | Committing)) != 0 || (!downloadLeft && toDownload > 0))
        {
            return new(InstallPhase.Installing, staged, toStage);
        }

        if ((flags & (UpdateRunning | Downloading)) != 0 || downloaded > 0)
        {
            return new(InstallPhase.Downloading, downloaded, toDownload);
        }

        // Started but not yet running is Steam's queue: another game is downloading first.
        return (flags & UpdateStarted) != 0 || toDownload > 0
            ? new(InstallPhase.Waiting, 0, toDownload)
            : new(InstallPhase.Waiting, 0, 0);
    }

    /// <summary>
    /// The bytes Steam has written of the game's files, and the bytes it will write in all, from a manifest.
    /// Steam's own progress bar ("Downloading 1 of 1, 23%") is the first over the second.
    /// </summary>
    public static (long Staged, long ToStage) StageCounts(string acf) =>
        (Number(acf, "BytesStaged") ?? 0, Number(acf, "BytesToStage") ?? 0);

    /// <summary>
    /// What the tile says while Steam is at work, which the manifest does not keep up with.
    /// </summary>
    /// <param name="fromFile">What the manifest says.</param>
    /// <param name="steamUpdating">Steam's own flag for this game, in the registry: it is being downloaded now.</param>
    /// <param name="stagedAtStart">The bytes written when the manifest was last written.</param>
    /// <param name="toStage">The bytes to write in all.</param>
    /// <param name="writtenSince">What Steam has written to disk since then, or null when that is not known
    /// (the library opened part way through the download, or Steam was restarted).</param>
    public static SteamInstallProgress Live(SteamInstallProgress fromFile, bool steamUpdating, long stagedAtStart, long toStage, long? writtenSince)
    {
        if (fromFile.Phase is InstallPhase.Paused or InstallPhase.Installed)
        {
            return fromFile;
        }

        // Pausing in Steam does not set the paused flag. Steam stops working on the game, which clears its
        // flag in the registry, and writes how far it got (Tomb Raider on the Ally, 2026-11-14: StateFlags
        // 1042 and half its bytes). A game with bytes down that Steam is not working on is paused, or queued
        // behind another after being started, which is the same to the player. One with none is waiting.
        if (!steamUpdating)
        {
            return fromFile.Phase is InstallPhase.Downloading or InstallPhase.Installing
                ? toStage > 0 ? new(InstallPhase.Paused, Math.Min(stagedAtStart, toStage), toStage) : fromFile with { Phase = InstallPhase.Paused }
                : fromFile;
        }

        if (writtenSince is not { } written || toStage <= 0)
        {
            return new(InstallPhase.Downloading, 0, 0);
        }

        // Steam writes a little of its own as well, so the count stops short of done until the file says so.
        var done = Math.Min(stagedAtStart + Math.Max(written, 0), toStage * 99 / 100);
        return new(InstallPhase.Downloading, done, toStage);
    }

    private static long? Number(string vdf, string name)
    {
        var m = Regex.Match(vdf, $"\"{Regex.Escape(name)}\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var value) ? value : null;
    }

    [GeneratedRegex(@"^appmanifest_(\d+)\.acf$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestName();
}
