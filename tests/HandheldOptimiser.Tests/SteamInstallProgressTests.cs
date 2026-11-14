using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Reading how far a Steam install has got from its appmanifest, which is what a game's tile in Ready to
/// install shows while Steam downloads it.
/// </summary>
public class SteamInstallProgressTests
{
    // Laid out as Steam writes it, with the fields the reader does not use left in around the ones it does.
    private static string Manifest(int flags, long toDownload = 0, long downloaded = 0, long toStage = 0, long staged = 0) => $$"""
        "AppState"
        {
        	"appid"		"620"
        	"Universe"		"1"
        	"name"		"Portal 2"
        	"StateFlags"		"{{flags}}"
        	"installdir"		"Portal 2"
        	"SizeOnDisk"		"0"
        	"StagingSize"		"0"
        	"buildid"		"0"
        	"BytesToDownload"		"{{toDownload}}"
        	"BytesDownloaded"		"{{downloaded}}"
        	"BytesToStage"		"{{toStage}}"
        	"BytesStaged"		"{{staged}}"
        	"InstalledDepots"
        	{
        	}
        }
        """;

    [Fact]
    public void An_installed_game_reads_as_installed_even_with_an_update_waiting()
    {
        Assert.Equal(InstallPhase.Installed, SteamInstallProgress.Read(Manifest(4))!.Value.Phase);
        Assert.Equal(InstallPhase.Installed, SteamInstallProgress.Read(Manifest(6, 100, 0))!.Value.Phase);
    }

    [Fact]
    public void A_download_under_way_says_how_far_it_has_got()
    {
        // UpdateRequired, UpdateRunning and UpdateStarted, as Steam has them while it downloads.
        var progress = SteamInstallProgress.Read(Manifest(1282, toDownload: 12_000_000_000, downloaded: 4_080_000_000))!.Value;

        Assert.Equal(InstallPhase.Downloading, progress.Phase);
        Assert.Equal(34, progress.Percent);
        Assert.Equal("Downloading 34%", progress.Text);
        Assert.Equal(0.34, progress.Fraction, 6);
    }

    [Fact]
    public void A_paused_download_says_so()
    {
        var progress = SteamInstallProgress.Read(Manifest(1538, toDownload: 1000, downloaded: 500))!.Value;

        Assert.Equal(InstallPhase.Paused, progress.Phase);
        Assert.Equal("Paused at 50%", progress.Text);
    }

    [Fact]
    public void A_queued_install_is_waiting()
    {
        var progress = SteamInstallProgress.Read(Manifest(1026, toDownload: 1000))!.Value;

        Assert.Equal(InstallPhase.Waiting, progress.Phase);
        Assert.Equal("Waiting to download", progress.Text);
    }

    [Fact]
    public void Once_downloaded_the_files_are_installing()
    {
        var progress = SteamInstallProgress.Read(Manifest(1282, toDownload: 1000, downloaded: 1000, toStage: 4000, staged: 1000))!.Value;

        Assert.Equal(InstallPhase.Installing, progress.Phase);
        Assert.Equal("Installing 25%", progress.Text);
    }

    [Fact]
    public void Staging_flags_mean_installing_whatever_the_counts_say()
    {
        Assert.Equal(InstallPhase.Installing, SteamInstallProgress.Read(Manifest(1282 | (1 << 21), toDownload: 0))!.Value.Phase);
        Assert.Equal("Installing", SteamInstallProgress.Read(Manifest(1282 | (1 << 22)))!.Value.Text);
    }

    [Fact]
    public void A_size_not_known_yet_shows_no_figure()
    {
        var progress = SteamInstallProgress.Read(Manifest(1282))!.Value;

        Assert.Equal(InstallPhase.Downloading, progress.Phase);
        Assert.Equal("Downloading", progress.Text);
        Assert.Equal(0, progress.Fraction);
    }

    [Fact]
    public void Nearly_done_is_not_rounded_up_to_a_hundred()
    {
        var progress = SteamInstallProgress.Read(Manifest(1282, toDownload: 1000, downloaded: 999))!.Value;

        Assert.Equal("Downloading 99%", progress.Text);
    }

    [Fact]
    public void A_half_written_file_is_not_read()
    {
        Assert.Null(SteamInstallProgress.Read("\"AppState\"\n{\n\t\"appid\"\t\t\"620\""));
        Assert.Null(SteamInstallProgress.Read(""));
    }

    [Theory]
    [InlineData("appmanifest_620.acf", "620")]
    [InlineData("APPMANIFEST_1086940.ACF", "1086940")]
    [InlineData("appmanifest_620.acf.tmp", null)]
    [InlineData("libraryfolders.vdf", null)]
    public void The_app_id_comes_from_the_file_name(string name, string? appId)
    {
        Assert.Equal(appId, SteamInstallProgress.AppIdFromFileName(name));
    }
}
