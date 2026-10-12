using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Which of SteamGridDB's answers is taken for a game or an app. The lists here are what it answered
/// for these searches on 2026-10-12, in its own order.
/// </summary>
public class ArtworkMatchTests
{
    private static readonly (long Id, string Name)[] Vortex =
    [
        (8257, "Vortex"), (5294546, "Vortex"), (5319915, "Vortex"), (5522436, "Vortex"), (7068, "Vortex Attack"),
        (1, "Vortex: The Gateway"), (2, "Vortex Rush"), (3, "Vortex Of Pain"),
        (5248806, "Vortex Mod Manager (Program)"), (4, "Vortex Attack EX")
    ];

    private static readonly (long Id, string Name)[] Chrome =
    [
        (37126, "Google Chrome (Program)"), (6624, "Google Earth VR"), (12058, "Google Spotlight Stories: Pearl")
    ];

    [Fact]
    public void AGameTakesTheFirstAnswer() =>
        Assert.Equal(8257, ArtworkMatch.Pick(Vortex, "Vortex", isApp: false, corrected: false));

    [Fact]
    public void AnAppTakesTheProgramAndNotAGameOfTheSameName() =>
        Assert.Equal(5248806, ArtworkMatch.Pick(Vortex, "Vortex", isApp: true, corrected: false));

    [Fact]
    public void AnAppTakesTheProgramListedFirst() =>
        Assert.Equal(37126, ArtworkMatch.Pick(Chrome, "Google Chrome", isApp: true, corrected: false));

    [Fact]
    public void AnAppTakesAProgramUnderALongerName()
    {
        (long, string)[] firefox = [(10, "Firefox Reality"), (11, "Mozilla Firefox (Program)")];

        Assert.Equal(11, ArtworkMatch.Pick(firefox, "Firefox", isApp: true, corrected: false));
    }

    [Fact]
    public void OfSeveralMarkedAnswersTheOneOfTheSameNameIsTaken()
    {
        (long, string)[] youTube = [(20, "YouTube VR"), (21, "YouTube Music (Website)"), (22, "YouTube (Website)")];

        Assert.Equal(22, ArtworkMatch.Pick(youTube, "YouTube", isApp: true, corrected: false));
    }

    [Fact]
    public void TheMarkIsReadWhateverItsCase()
    {
        (long, string)[] notepad = [(30, "Notepad (program)"), (31, "Windows Notepad (Program)"), (32, "Notepadqq (Program)")];

        Assert.Equal(30, ArtworkMatch.Pick(notepad, "notepad", isApp: true, corrected: false));
    }

    [Fact]
    public void AnAppTakesTheOneUnmarkedAnswerOfExactlyItsName()
    {
        (long, string)[] code = [(40, "Visual Studio Code"), (41, "Visual Novel Maker"), (42, "Code Vein")];

        Assert.Equal(40, ArtworkMatch.Pick(code, "Visual Studio Code", isApp: true, corrected: false));
    }

    [Fact]
    public void AnAppTakesNothingWhenOnlyGamesShareItsName()
    {
        (long, string)[] games = [(8257, "Vortex"), (5294546, "Vortex"), (7068, "Vortex Attack")];

        Assert.Null(ArtworkMatch.Pick(games, "Vortex", isApp: true, corrected: false));
    }

    [Fact]
    public void AnAppTakesNothingFromAnswersThatOnlyResembleItsName()
    {
        (long, string)[] others = [(6624, "Google Earth VR"), (12058, "Google Spotlight Stories: Pearl")];

        Assert.Null(ArtworkMatch.Pick(others, "Google Drive", isApp: true, corrected: false));
    }

    [Fact]
    public void ANameThePlayerTypedTakesTheFirstAnswerWhenNoneIsMarked()
    {
        (long, string)[] games = [(8257, "Vortex"), (5294546, "Vortex")];

        Assert.Equal(8257, ArtworkMatch.Pick(games, "Vortex", isApp: true, corrected: true));
    }

    [Fact]
    public void ANameThePlayerTypedStillPrefersTheProgram() =>
        Assert.Equal(5248806, ArtworkMatch.Pick(Vortex, "Vortex Mod Manager (Program)", isApp: true, corrected: true));

    [Fact]
    public void NothingIsTakenFromNoAnswers()
    {
        Assert.Null(ArtworkMatch.Pick([], "Vortex", isApp: true, corrected: false));
        Assert.Null(ArtworkMatch.Pick([], "Vortex", isApp: false, corrected: false));
    }
}
