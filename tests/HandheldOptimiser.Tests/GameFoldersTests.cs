using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public class GameFoldersTests
{
    private static Game Item(string key, bool installed = true, bool app = false) =>
        new(key, key, GameStore.Other, null, null, key, Installed: installed, IsApp: app);

    private static string? FolderOf(Game game) => game.Key switch
    {
        "fortnite" => @"C:\Program Files\Epic Games\Fortnite\",
        "fortnite again" => @"c:\program files\epic games\fortnite\",
        "chrome" => @"C:\Program Files\Google\Chrome\Application\",
        "stardew" => @"D:\Steam\steamapps\common\Stardew Valley\",
        "uninstalled" => @"D:\Steam\steamapps\common\Old\",
        _ => null
    };

    [Fact]
    public void Installed_games_are_listed_once_and_apps_never()
    {
        var folders = GameFolders.From(
            [Item("fortnite"), Item("chrome", app: true), Item("stardew"), Item("fortnite again"), Item("uninstalled", installed: false), Item("no folder")],
            FolderOf);

        Assert.Equal([@"C:\Program Files\Epic Games\Fortnite\", @"D:\Steam\steamapps\common\Stardew Valley\"], folders);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", true)]
    [InlineData(@"c:\program files\epic games\fortnite\launcher.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite2\game.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    public void Only_programs_inside_a_listed_folder_count(string program, bool counts) =>
        Assert.Equal(counts, GameLock.InFolders(program, [@"C:\Program Files\Epic Games\Fortnite\", @"D:\Steam\steamapps\common\Stardew Valley\"]));

    [Fact]
    public void A_folder_saved_without_its_separator_still_only_matches_inside_it() =>
        Assert.False(GameLock.InFolders(@"C:\Games\Fortnite2\game.exe", [@"C:\Games\Fortnite"]));
}
