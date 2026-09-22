namespace HandheldOptimiser.Models;

/// <summary>
/// A one-shot maintenance job run from a button rather than a toggle. Unlike a <see cref="Tweak"/> there
/// is no "on" state to detect and nothing to journal: deleted caches and flushed DNS entries cannot be put
/// back, and do not need to be.
/// </summary>
public sealed class PowerAction
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Segoe Fluent Icons glyph for the button.</summary>
    public string Glyph { get; init; } = "";

    public string? Warning { get; init; }
    public bool RequiresReboot { get; init; }

    /// <summary>
    /// Whether to take a restore point first. Only meaningful for actions that change system
    /// configuration; System Restore does not cover user files, so it is pointless before a cache clean.
    /// </summary>
    public bool CreateRestorePoint { get; init; }

    /// <summary>Rough duration shown on the button, so a multi-minute job does not look hung.</summary>
    public string? DurationHint { get; init; }

    /// <summary>
    /// Optional check run before anything else, including the confirmation prompt and restore point.
    /// Returns a reason to refuse, or null to allow the action.
    /// </summary>
    public Func<string?>? Precheck { get; init; }

    public required Func<TweakContext, IProgress<string>?, CancellationToken, Task<TweakResult>> Execute { get; init; }
}
