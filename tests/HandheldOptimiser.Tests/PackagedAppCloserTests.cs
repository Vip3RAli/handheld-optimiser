using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

public class PackagedAppCloserTests
{
    private static readonly UpdateItem Claude =
        new(UpdateSource.Winget, "Anthropic.Claude", "Claude", "2.37337.1.0", "2.42304.2", "winget");

    private const string OldFolder = "Claude_2.37337.1.0_x64__pzs8sxrjxfjjc";
    private const string NewFolder = "Claude_2.42304.2.0_x64__pzs8sxrjxfjjc";
    private const string Terminal = "Microsoft.WindowsTerminal_1.20.11781.0_x64__8wekyb3d8bbwe";
    private const string Apps = @"C:\Program Files\WindowsApps\";

    /// <summary>What this Ally had running on 2026-10-23 while the Claude update waited for Claude to close.</summary>
    private static readonly string[] Running =
    [
        $@"PROC|11784|{Apps}{OldFolder}\app\Claude.exe",
        $@"PROC|3560|{Apps}{OldFolder}\app\Claude.exe",
        $@"PROC|9000|{Apps}{Terminal}\WindowsTerminal.exe",
        $@"SVC|CoworkVMService|Running|""{Apps}{OldFolder}\app\resources\cowork-svc.exe""",
    ];

    /// <summary>The folders on disk then: both Claude versions, the new one staged beside the old.</summary>
    private static Func<string, bool> OnDisk(params string[] folders) =>
        f => folders.Contains(f, StringComparer.OrdinalIgnoreCase);

    private static readonly Func<string, bool> Staged = OnDisk(OldFolder, NewFolder, Terminal);

    [Fact]
    public void Finds_what_is_still_running_from_the_old_version()
    {
        var app = PackagedAppCloser.Read(Running, Claude, Staged);

        Assert.NotNull(app);
        Assert.Equal(OldFolder, app.PackageFolder);
        Assert.Equal(2, app.ProcessCount);
        Assert.Equal(["CoworkVMService"], app.Services);
        Assert.Equal(["CoworkVMService"], app.RunningServices);
    }

    [Fact]
    public void Finds_nothing_when_no_newer_version_is_staged()
    {
        Assert.Null(PackagedAppCloser.Read(Running, Claude, OnDisk(OldFolder, Terminal)));
    }

    [Fact]
    public void Finds_nothing_when_the_staged_version_is_from_another_publisher()
    {
        Assert.Null(PackagedAppCloser.Read(Running, Claude, OnDisk(OldFolder, "Claude_2.42304.2.0_x64__aaaaaaaaaaaaa")));
    }

    [Fact]
    public void Finds_nothing_when_nothing_of_it_is_running()
    {
        var lines = Running.Where(l => !l.Contains(OldFolder, StringComparison.Ordinal));

        Assert.Null(PackagedAppCloser.Read(lines, Claude, Staged));
    }

    [Fact]
    public void Finds_a_service_alone_holding_the_package()
    {
        var lines = Running.Where(l => !l.StartsWith("PROC|", StringComparison.Ordinal) || !l.Contains(OldFolder, StringComparison.Ordinal));

        var app = PackagedAppCloser.Read(lines, Claude, Staged);

        Assert.NotNull(app);
        Assert.Equal(0, app.ProcessCount);
        Assert.Equal(["CoworkVMService"], app.RunningServices);
    }

    [Fact]
    public void Leaves_out_a_service_whose_name_could_not_be_passed_safely()
    {
        var lines = Running.Append($@"SVC|Bad'Name|Running|{Apps}{OldFolder}\svc.exe");

        var app = PackagedAppCloser.Read(lines, Claude, Staged);

        Assert.NotNull(app);
        Assert.Equal(["CoworkVMService"], app.Services);
    }

    [Fact]
    public void A_stopped_service_is_stopped_but_not_started_again()
    {
        var lines = Running.Select(l => l.Replace("|Running|", "|Stopped|", StringComparison.Ordinal));

        var app = PackagedAppCloser.Read(lines, Claude, Staged);

        Assert.NotNull(app);
        Assert.Equal(["CoworkVMService"], app.Services);
        Assert.Empty(app.RunningServices);
    }

    [Theory]
    [InlineData("2.42304.2", "2.42304.2.0")]
    [InlineData("2.37337.1.0", "2.37337.1.0")]
    [InlineData("26", "26.0.0.0")]
    [InlineData("Unknown", null)]
    [InlineData("1.2.3.4.5", null)]
    [InlineData("2.37337.1…", null)]
    [InlineData(null, null)]
    public void Writes_a_version_the_way_a_package_folder_does(string? version, string? expected)
    {
        Assert.Equal(expected, PackagedAppCloser.PackageVersion(version));
    }
}
