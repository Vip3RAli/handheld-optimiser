namespace HandheldOptimiser.Models;

public sealed class TweakResult
{
    public required string TweakId { get; init; }
    public required ResultStatus Status { get; init; }
    public string? Message { get; init; }
    public bool RebootRequired { get; init; }

    public bool IsFailure => Status is ResultStatus.Failed or ResultStatus.Blocked;

    public static TweakResult Ok(string tweakId, string? message = null, bool rebootRequired = false) =>
        new() { TweakId = tweakId, Status = ResultStatus.Success, Message = message, RebootRequired = rebootRequired };

    public static TweakResult NoChange(string tweakId, string? message = null) =>
        new() { TweakId = tweakId, Status = ResultStatus.NoChangeNeeded, Message = message };

    public static TweakResult Blocked(string tweakId, string reason) =>
        new() { TweakId = tweakId, Status = ResultStatus.Blocked, Message = reason };

    public static TweakResult Fail(string tweakId, string reason) =>
        new() { TweakId = tweakId, Status = ResultStatus.Failed, Message = reason };
}

/// <summary>
/// Everything needed to put one tweak back exactly how it was found.
/// </summary>
public sealed class TweakJournalEntry
{
    public string TweakId { get; set; } = string.Empty;
    public string TweakName { get; set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; set; }
    public List<RegistryValueSnapshot> RegistrySnapshots { get; set; } = [];

    /// <summary>Free-form captured state for tweaks that are not plain registry writes (DISM features, bcdedit, tasks).</summary>
    public Dictionary<string, string> CapturedState { get; set; } = [];
}

public sealed class TweakJournal
{
    public int SchemaVersion { get; set; } = 1;
    public List<TweakJournalEntry> Entries { get; set; } = [];
}
