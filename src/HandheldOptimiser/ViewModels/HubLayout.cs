using HandheldOptimiser.Models;

namespace HandheldOptimiser.ViewModels;

/// <summary>What a section of a goal's page shows.</summary>
public enum SectionKind
{
    Tweaks,
    GpuMemory,
    HomeApp,
    Startup,
    PreinstalledApps,
    Backup,
    Jobs,
    Health,
    Updates,
    Runtimes
}

/// <summary>
/// One section of a goal's page. <paramref name="Heading"/> is empty where the page draws no heading
/// over it, and <paramref name="Category"/> is set only for a section of tweaks.
/// </summary>
public sealed record SectionSpec(SectionKind Kind, string Heading, TweakCategory? Category = null);

/// <summary>
/// One goal: a card on the Dashboard and the page it opens. <paramref name="Hint"/> is the card's last
/// line, which says what is inside before the card is pressed.
/// </summary>
public sealed record GoalSpec(string Title, string Glyph, string Hint, IReadOnlyList<SectionSpec> Sections);

/// <summary>
/// The map of the app: the eight goals the Dashboard shows, in order, and what each one's page holds.
/// The app is organised by what the player wants to do, not by which part of Windows a tweak changes,
/// so this is the one place that says where a thing lives.
/// </summary>
public static class HubLayout
{
    private static SectionSpec Tweaks(string heading, TweakCategory category) => new(SectionKind.Tweaks, heading, category);

    public static IReadOnlyList<GoalSpec> Goals { get; } =
    [
        new("Performance", "", "Games, system, network",
        [
            new(SectionKind.GpuMemory, "GPU memory"),
            Tweaks("Games", TweakCategory.Games),
            Tweaks("System", TweakCategory.System),
            Tweaks("Network", TweakCategory.Network)
        ]),

        new("Battery & sleep", "", "Hibernate, wake, standby drain",
        [
            Tweaks(string.Empty, TweakCategory.Sleep)
        ]),

        new("Clean up", "", "Privacy, ads, background, services",
        [
            Tweaks("Privacy", TweakCategory.Privacy),
            Tweaks("Ads & assistants", TweakCategory.AdsAndAssistants),
            Tweaks("Background activity", TweakCategory.BackgroundActivity),
            Tweaks("Services", TweakCategory.Services)
        ]),

        new("Apps", "", "Startup and preinstalled apps",
        [
            new(SectionKind.Startup, "Startup"),
            new(SectionKind.PreinstalledApps, "Preinstalled apps")
        ]),

        new("Full screen", "", "Home app, sign-in, touch",
        [
            new(SectionKind.HomeApp, "Home app"),
            Tweaks("Sign-in and Armoury Crate", TweakCategory.FullScreen),
            Tweaks("Touch & desktop", TweakCategory.TouchAndDesktop)
        ]),

        new("Maintenance & backup", "", "Restore point, undo, clean-up jobs",
        [
            new(SectionKind.Backup, "Backup"),
            new(SectionKind.Jobs, "Jobs"),
            Tweaks("Storage", TweakCategory.Storage)
        ]),

        new("Health", "", "Sleep, power plan, storage",
        [
            new(SectionKind.Health, string.Empty)
        ]),

        new("Updates", "", "Windows, apps, game runtimes",
        [
            new(SectionKind.Updates, string.Empty),
            new(SectionKind.Runtimes, "Game runtimes")
        ])
    ];

    /// <summary>The tweaks on a goal's page, section by section, each section in the catalog's order.</summary>
    public static IReadOnlyList<Tweak> TweaksOf(GoalSpec goal, IReadOnlyList<Tweak> all) =>
        [.. goal.Sections
            .Where(s => s.Category is not null)
            .SelectMany(s => all.Where(t => t.Category == s.Category))];
}
