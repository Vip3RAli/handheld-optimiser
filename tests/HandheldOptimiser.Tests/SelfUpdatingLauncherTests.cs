using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Game launchers update themselves, and winget running their old installer over them broke the Epic Games
/// Launcher on the Ally (2026-10-12), so the Updates page never offers them.
/// </summary>
public class SelfUpdatingLauncherTests
{
    private static UpdateItem Row(string id, string source = "winget") =>
        new(UpdateSource.Winget, id, id, "1.0", "2.0", source);

    [Fact]
    public void Launchers_and_Store_apps_are_left_out_and_other_apps_stay()
    {
        var offered = WingetUpdates.Offered(
            [Row("EpicGames.EpicGamesLauncher"), Row("Discord.Discord"), Row("valve.steam"), Row("9N0DX20HK701", "msstore")]);

        Assert.Equal(["Discord.Discord"], offered.Select(i => i.Key));
    }

    [Theory]
    [InlineData("EpicGames.EpicGamesLaun…", true)]
    [InlineData("ElectronicArts.EADesk…", true)]
    [InlineData("Microsoft.VCRedist.2015…", false)]
    [InlineData("EpicGames.EpicGamesLauncherBeta", false)]
    public void An_ID_winget_cut_short_is_still_recognised(string id, bool expected) =>
        Assert.Equal(expected, WingetUpdates.IsSelfUpdating(id));
}
