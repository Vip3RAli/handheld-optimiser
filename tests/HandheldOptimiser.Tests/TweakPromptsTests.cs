using HandheldOptimiser.Models;
using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// A tweak's warning is no longer on show all the time: it opens when the tile is pressed. So any
/// tweak that carries one has to ask before it is switched on, and Apply all has to name them.
/// </summary>
public sealed class TweakPromptsTests
{
    private static Tweak MakeTweak(string name, RiskLevel risk = RiskLevel.Safe, string? warning = null) => new()
    {
        Id = name,
        Name = name,
        Description = "Test tweak",
        Category = TweakCategory.Games,
        Risk = risk,
        Warning = warning
    };

    [Fact]
    public void A_safe_tweak_with_no_warning_does_not_ask() =>
        Assert.False(TweakPrompts.AsksFirst(MakeTweak("plain"), isForThisDevice: true));

    [Fact]
    public void A_safe_tweak_with_a_warning_asks() =>
        Assert.True(TweakPrompts.AsksFirst(MakeTweak("warned", warning: "Starting up takes longer."), isForThisDevice: true));

    [Fact]
    public void A_blank_warning_is_no_warning() =>
        Assert.False(TweakPrompts.AsksFirst(MakeTweak("blank", warning: "  "), isForThisDevice: true));

    [Theory]
    [InlineData(RiskLevel.SecurityTradeoff)]
    [InlineData(RiskLevel.Breaking)]
    public void A_high_risk_asks_without_a_warning(RiskLevel risk) =>
        Assert.True(TweakPrompts.AsksFirst(MakeTweak("risky", risk), isForThisDevice: true));

    [Fact]
    public void Another_devices_workaround_asks() =>
        Assert.True(TweakPrompts.AsksFirst(MakeTweak("elsewhere"), isForThisDevice: false));

    [Fact]
    public void Apply_all_names_every_warned_tweak_in_order()
    {
        Tweak[] pending =
        [
            MakeTweak("plain"),
            MakeTweak("warned", RiskLevel.Moderate, "Removes a feature."),
            MakeTweak("moderate", RiskLevel.Moderate),
            MakeTweak("breaking", RiskLevel.Breaking)
        ];

        Assert.Equal(["warned", "breaking"], TweakPrompts.WarnedNames(pending));
    }
}
