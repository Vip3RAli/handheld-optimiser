namespace HandheldOptimiser.Models;

/// <summary>Where an update comes from. Each source has its own installer and its own section on the Updates page.</summary>
public enum UpdateSource
{
    WindowsUpdate,
    Store,
    Winget
}

/// <summary>
/// One update that is waiting to be installed.
/// </summary>
/// <param name="Key">
/// What the source installs it by: the Windows Update ID (a GUID), the Store package family name, or the
/// winget package ID. Each source checks the shape of its keys before putting one into a command.
/// </param>
public sealed record UpdateItem(
    UpdateSource Source,
    string Key,
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    string? Detail = null)
{
    /// <summary>Identifies the update in the skip list, which holds items from every source.</summary>
    public string SkipKey => $"{Source}:{Key}";
}

/// <summary>What one source's check found.</summary>
/// <param name="Error">Set when the check itself failed, so the section can say so instead of "Up to date".</param>
public sealed record UpdateScan(IReadOnlyList<UpdateItem> Items, string? Error = null)
{
    public static UpdateScan Failed(string error) => new([], error);
}

public enum UpdateOutcomeKind
{
    Updated,

    /// <summary>The source says there was nothing to install after all, usually because it updated itself in the meantime.</summary>
    AlreadyCurrent,

    Failed
}

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string Message, bool RebootRequired = false)
{
    public static UpdateOutcome Ok(string message, bool rebootRequired = false) =>
        new(UpdateOutcomeKind.Updated, message, rebootRequired);

    public static UpdateOutcome Current(string message) => new(UpdateOutcomeKind.AlreadyCurrent, message);

    public static UpdateOutcome Fail(string message) => new(UpdateOutcomeKind.Failed, message);
}

/// <summary>
/// Reported while an install runs. Sources report from whatever thread reads the installer's output,
/// so the page receives these through an <see cref="IProgress{T}"/> made on the UI thread.
/// </summary>
/// <param name="Outcome">Null while the item is still in progress; <paramref name="Step"/> then says what it is doing.</param>
public sealed record UpdateProgress(UpdateItem Item, string Step, UpdateOutcome? Outcome = null);
