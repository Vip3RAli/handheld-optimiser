using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

public class WingetUpgradeTableTests
{
    private const string English =
        "   - \r   \\ \r                                                                                                 \r" +
        "Name                                   Id                           Version        Available      Source\r\n" +
        "---------------------------------------------------------------------------------------------------------\r\n" +
        "Discord                                Discord.Discord              1.0.9163       1.0.9164       winget\r\n" +
        "Microsoft Edge                         Microsoft.Edge               129.0.2792.65  129.0.2792.79  winget\r\n" +
        "Microsoft Visual C++ 2015-2022 Redist… Microsoft.VCRedist.2015+.x64 14.38.33135.0  14.40.33810.0  winget\r\n" +
        "Windows Terminal                       9N0DX20HK701                 1.20.11781.0   1.21.2361.0    msstore\r\n" +
        "4 upgrades available.\r\n" +
        "\r\n" +
        "The following packages have an upgrade available, but require explicit targeting for upgrade:\r\n" +
        "Name   Id           Version Available Source\r\n" +
        "--------------------------------------------\r\n" +
        "Steam  Valve.Steam  2.10    3.0       winget\r\n";

    [Fact]
    public void Reads_every_row_of_the_first_table()
    {
        var items = WingetUpdates.ParseUpgradeTable(English);

        Assert.Equal(
            ["Discord.Discord", "Microsoft.Edge", "Microsoft.VCRedist.2015+.x64", "9N0DX20HK701"],
            items.Select(i => i.Key));

        var edge = items[1];
        Assert.Equal(UpdateSource.Winget, edge.Source);
        Assert.Equal("Microsoft Edge", edge.Name);
        Assert.Equal("129.0.2792.65", edge.InstalledVersion);
        Assert.Equal("129.0.2792.79", edge.AvailableVersion);
        Assert.Equal("winget", edge.Detail);
        Assert.Equal("msstore", items[3].Detail);
    }

    [Fact]
    public void Leaves_out_pinned_packages_listed_under_the_table()
    {
        Assert.DoesNotContain(WingetUpdates.ParseUpgradeTable(English), i => i.Key == "Valve.Steam");
    }

    [Fact]
    public void Finds_columns_by_position_when_the_header_is_translated()
    {
        const string german =
            "Name              ID                 Version  Verfügbar Quelle\n" +
            "--------------------------------------------------------------\n" +
            "7-Zip 23.01 (x64) 7zip.7zip          23.01    24.08     winget\n";

        var item = Assert.Single(WingetUpdates.ParseUpgradeTable(german));
        Assert.Equal("7zip.7zip", item.Key);
        Assert.Equal("24.08", item.AvailableVersion);
    }

    [Fact]
    public void Keeps_an_id_winget_cut_short()
    {
        const string table =
            "Name           Id                       Version Available Source\n" +
            "----------------------------------------------------------------\n" +
            "Some Long App  Publisher.SomeVeryLongA… 1.0     2.0       winget\n";

        var item = Assert.Single(WingetUpdates.ParseUpgradeTable(table));
        Assert.Equal("Publisher.SomeVeryLongA…", item.Key);
    }

    [Fact]
    public void Refuses_an_id_that_winget_would_read_as_an_option()
    {
        const string table =
            "Name   Id          Version Available Source\n" +
            "-------------------------------------------\n" +
            "Bad    --override  1.0     2.0       winget\n";

        Assert.Empty(WingetUpdates.ParseUpgradeTable(table));
    }

    [Fact]
    public void Returns_nothing_when_there_are_no_updates()
    {
        Assert.Empty(WingetUpdates.ParseUpgradeTable("No installed package found matching input criteria.\r\n"));
        Assert.Empty(WingetUpdates.ParseUpgradeTable(string.Empty));
    }
}
