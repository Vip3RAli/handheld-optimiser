using HandheldOptimiser.Services;
using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// What a card on the Dashboard says about its page. A card counts every tweak on the page, but its dot
/// only turns yellow for a recommended one left off: the rest are trade-offs the player may not want.
/// </summary>
public sealed class CardStatusTests
{
    [Fact]
    public void All_recommended_on_is_green() => Assert.Equal(
        new CardStatus("17 of 19 on", CardLevel.Good),
        CardStatus.ForTweaks(new TweakSummary(17, 19, 19), new TweakSummary(12, 12, 12)));

    [Fact]
    public void A_recommended_tweak_left_off_is_yellow() => Assert.Equal(
        new CardStatus("17 of 19 on", CardLevel.Attention),
        CardStatus.ForTweaks(new TweakSummary(17, 19, 19), new TweakSummary(11, 12, 12)));

    [Fact]
    public void A_page_with_nothing_recommended_is_green() => Assert.Equal(
        new CardStatus("5 of 6 on", CardLevel.Good),
        CardStatus.ForTweaks(new TweakSummary(5, 6, 6), new TweakSummary(0, 0, 0)));

    [Fact]
    public void A_state_still_out_says_checking() => Assert.Equal(
        CardStatus.Checking,
        CardStatus.ForTweaks(new TweakSummary(3, 18, 19), new TweakSummary(3, 12, 12)));

    [Fact]
    public void Health_with_nothing_to_look_at() => Assert.Equal(
        new CardStatus("Nothing needs a look", CardLevel.Good),
        CardStatus.ForHealth(0, HealthStatus.Good));

    [Fact]
    public void Health_with_warnings() => Assert.Equal(
        new CardStatus("2 to look at", CardLevel.Attention),
        CardStatus.ForHealth(2, HealthStatus.Warning));

    [Fact]
    public void Health_with_a_fault() => Assert.Equal(
        new CardStatus("1 to look at", CardLevel.Bad),
        CardStatus.ForHealth(1, HealthStatus.Bad));

    [Fact]
    public void Health_not_read_yet() => Assert.Equal(
        CardStatus.Checking,
        CardStatus.ForHealth(0, HealthStatus.Unknown));

    [Fact]
    public void Updates_waiting() => Assert.Equal(
        new CardStatus("3 waiting", CardLevel.Attention),
        CardStatus.ForUpdates(3));

    [Fact]
    public void No_updates() => Assert.Equal(
        new CardStatus("Up to date", CardLevel.Good),
        CardStatus.ForUpdates(0));
}
