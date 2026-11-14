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
/// that file as soon as an install is confirmed in its dialog and keeps the byte counts in it up to date
/// as it downloads, so the library can follow an install it handed to Steam without asking Steam anything.
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

    private static long? Number(string vdf, string name)
    {
        var m = Regex.Match(vdf, $"\"{Regex.Escape(name)}\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out var value) ? value : null;
    }

    [GeneratedRegex(@"^appmanifest_(\d+)\.acf$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestName();
}
