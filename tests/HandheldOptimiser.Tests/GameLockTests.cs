using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

public class GameLockTests
{
    private const string Fortnite = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
    private const string Editor = @"C:\Program Files\Editor\editor.exe";
    private static readonly DateTime Start = new(2026, 10, 15, 22, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void A_program_holding_a_game_frame_rate_for_10_seconds_is_locked_onto_by_its_folder()
    {
        var game = new GameLock();

        Assert.Null(game.Observe(At(0), Fortnite, 60));
        Assert.Null(game.Observe(At(9), Fortnite, 58));
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\", game.Observe(At(10), Fortnite, 61));
    }

    [Fact]
    public void Dropping_under_the_game_frame_rate_starts_the_wait_again()
    {
        var game = new GameLock();
        game.Observe(At(0), Fortnite, 60);
        game.Observe(At(5), Fortnite, 12);
        game.Observe(At(6), Fortnite, 60);

        Assert.Null(game.Observe(At(15), Fortnite, 60));
        Assert.NotNull(game.Observe(At(16), Fortnite, 60));
    }

    [Fact]
    public void Another_program_taking_over_starts_the_wait_again()
    {
        var game = new GameLock();
        game.Observe(At(0), Editor, 30);
        game.Observe(At(6), Fortnite, 60);

        Assert.Null(game.Observe(At(12), Fortnite, 60));
        Assert.EndsWith(@"\Win64\", game.Observe(At(16), Fortnite, 60));
    }

    [Fact]
    public void A_desktop_program_drawing_now_and_then_is_never_locked_onto()
    {
        var game = new GameLock();
        for (var second = 0; second < 120; second++)
        {
            Assert.Null(game.Observe(At(second), Editor, second % 4 == 0 ? 8 : 1));
        }
    }

    [Fact]
    public void Nothing_drawing_is_never_locked_onto()
    {
        var game = new GameLock();
        game.Observe(At(0), null, 60);

        Assert.Null(game.Observe(At(20), null, 60));
    }
}
