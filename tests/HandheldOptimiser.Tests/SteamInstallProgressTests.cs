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

    [Fact]
    public void A_manifest_says_how_much_there_is_to_write()
    {
        Assert.Equal((1000L, 4000L), SteamInstallProgress.StageCounts(Manifest(1282, toStage: 4000, staged: 1000)));
        Assert.Equal((0L, 0L), SteamInstallProgress.StageCounts("\"AppState\" { }"));
    }

    // House Flipper on the Ally, 2026-11-14: Steam showed 1.3 GB of 6.6 GB downloaded and 23% installed,
    // while its manifest still said what it said as the download began.
    private static readonly string Started = Manifest(1042, toDownload: 7_070_267_984, toStage: 13_796_365_148);

    [Fact]
    public void While_Steam_downloads_the_figure_is_what_it_has_written_since_the_manifest()
    {
        var fromFile = SteamInstallProgress.Read(Started)!.Value;
        Assert.Equal(InstallPhase.Waiting, fromFile.Phase);

        var live = SteamInstallProgress.Live(fromFile, steamUpdating: true, stagedAtStart: 0, toStage: 13_796_365_148, writtenSince: 3_200_000_000);

        Assert.Equal(InstallPhase.Downloading, live.Phase);
        Assert.Equal("Downloading 23%", live.Text);
    }

    [Fact]
    public void Without_a_starting_count_the_download_shows_no_figure()
    {
        var live = SteamInstallProgress.Live(SteamInstallProgress.Read(Started)!.Value, steamUpdating: true, 0, 13_796_365_148, writtenSince: null);

        Assert.Equal("Downloading", live.Text);
        Assert.Equal(0, live.Fraction);
    }

    [Fact]
    public void A_game_Steam_is_not_working_on_is_as_its_manifest_says()
    {
        var fromFile = SteamInstallProgress.Read(Started)!.Value;

        Assert.Equal("Waiting to download", SteamInstallProgress.Live(fromFile, steamUpdating: false, 0, 13_796_365_148, 5_000_000_000).Text);
    }

    [Fact]
    public void A_download_Steam_has_stopped_working_on_is_paused_where_it_stopped()
    {
        // Tomb Raider on the Ally, 2026-11-14, paused in Steam: no paused flag, half the bytes, and Steam's
        // flag in the registry cleared.
        var acf = Manifest(1042, toDownload: 13_633_061_392, downloaded: 6_944_690_000, toStage: 15_318_582_472, staged: 7_704_601_875);
        var fromFile = SteamInstallProgress.Read(acf)!.Value;
        var (staged, toStage) = SteamInstallProgress.StageCounts(acf);

        var live = SteamInstallProgress.Live(fromFile, steamUpdating: false, staged, toStage, writtenSince: 0);

        Assert.Equal(InstallPhase.Paused, live.Phase);
        Assert.Equal("Paused at 50%", live.Text);
        Assert.Equal("Paused", DownloadsPresenter.PillText([new DownloadRow("203160", "Tomb Raider", live)]));
    }

    [Fact]
    public void Paused_comes_from_the_manifest_whatever_has_been_written()
    {
        var paused = SteamInstallProgress.Read(Manifest(1538, toDownload: 1000, downloaded: 500))!.Value;

        Assert.Equal("Paused at 50%", SteamInstallProgress.Live(paused, steamUpdating: true, 0, 4000, 3000).Text);
    }

    [Fact]
    public void Steam_writing_more_than_the_game_stops_short_of_done()
    {
        var live = SteamInstallProgress.Live(SteamInstallProgress.Read(Started)!.Value, true, 1000, 4000, writtenSince: 9000);

        Assert.Equal("Downloading 99%", live.Text);
    }

    [Fact]
    public void The_status_pill_gives_the_one_download_s_figure_or_how_many_there_are()
    {
        static DownloadRow Row(string id, InstallPhase phase, long done = 0, long total = 0) =>
            new(id, "Game " + id, new SteamInstallProgress(phase, done, total));

        Assert.Equal(string.Empty, DownloadsPresenter.PillText([]));
        Assert.Equal("23%", DownloadsPresenter.PillText([Row("1", InstallPhase.Downloading, 23, 100)]));
        Assert.Equal(string.Empty, DownloadsPresenter.PillText([Row("1", InstallPhase.Downloading)]));
        Assert.Equal("Paused", DownloadsPresenter.PillText([Row("1", InstallPhase.Paused, 5, 10)]));
        Assert.Equal("Queued", DownloadsPresenter.PillText([Row("1", InstallPhase.Waiting)]));
        Assert.Equal("2 games", DownloadsPresenter.PillText([Row("1", InstallPhase.Downloading, 1, 2), Row("2", InstallPhase.Waiting)]));
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
