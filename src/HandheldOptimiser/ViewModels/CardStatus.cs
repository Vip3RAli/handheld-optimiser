using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>The colour of the dot on a Dashboard card.</summary>
public enum CardLevel
{
    /// <summary>Not known yet. Grey.</summary>
    Checking,

    /// <summary>Nothing left to do here. Green.</summary>
    Good,

    /// <summary>Something is worth a look. Yellow.</summary>
    Attention,

    /// <summary>Something is wrong. Red.</summary>
    Bad,

    /// <summary>A fact that is neither good nor bad, such as which home app is chosen. Cyan.</summary>
    Neutral
}

/// <summary>The one line a Dashboard card says about its page, and the colour of the dot beside it.</summary>
public sealed record CardStatus(string Text, CardLevel Level)
{
    public static CardStatus Checking { get; } = new("Checking…", CardLevel.Checking);

    public static CardStatus Neutral(string text) => new(text, CardLevel.Neutral);

    /// <summary>
    /// A page of tweaks. The text counts every tweak on the page. The dot only turns yellow for a
    /// recommended one left off, since the others are trade-offs the player may well not want.
    /// </summary>
    public static CardStatus ForTweaks(TweakSummary all, TweakSummary recommended) =>
        !all.IsComplete ? Checking
            : new($"{all.Applied} of {all.Total} on",
                recommended.Applied == recommended.Total ? CardLevel.Good : CardLevel.Attention);

    /// <summary>The health report: how many rows are yellow or red, and the worst of them.</summary>
    public static CardStatus ForHealth(int toLookAt, HealthStatus worst) => worst switch
    {
        HealthStatus.Unknown => Checking,
        _ when toLookAt == 0 => new("Nothing needs a look", CardLevel.Good),
        HealthStatus.Bad => new($"{toLookAt} to look at", CardLevel.Bad),
        _ => new($"{toLookAt} to look at", CardLevel.Attention)
    };

    /// <summary>
    /// Updates: how many are waiting. Finding none is only "Up to date" when every source answered:
    /// a handheld is often offline as the app opens, and a check that failed found nothing either.
    /// </summary>
    public static CardStatus ForUpdates(int waiting, bool couldNotCheck) =>
        waiting > 0 ? new($"{waiting} waiting", CardLevel.Attention)
            : couldNotCheck ? new("Could not check", CardLevel.Attention)
            : new("Up to date", CardLevel.Good);
}
