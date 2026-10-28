using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>How the launcher's --radeon mode says RSR and AFMF through its exit code, and how TDP Engine reads it.</summary>
public class RadeonStateTests
{
    public static TheoryData<bool?, bool?> States => new()
    {
        { null, null },
        { false, false },
        { true, false },
        { false, true },
        { true, true },
        { null, true },
        { true, null },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Every_state_comes_back_as_it_went(bool? rsr, bool? afmf) =>
        Assert.Equal(new RadeonState(rsr, afmf), RadeonState.FromExitCode(new RadeonState(rsr, afmf).ToExitCode()));

    [Fact]
    public void Both_supported_and_off_is_what_the_Ally_answered() =>
        Assert.Equal(0x45, new RadeonState(false, false).ToExitCode());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1073741819)]
    [InlineData(0x50)]
    public void Other_exit_codes_say_nothing(int code) =>
        Assert.Null(RadeonState.FromExitCode(code));
}
