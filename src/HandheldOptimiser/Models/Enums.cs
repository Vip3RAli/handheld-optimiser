namespace HandheldOptimiser.Models;

public enum TweakCategory
{
    Gaming,
    Debloat,
    Privacy,
    Startup,
    CpuKernel,
    Network,
    Services,
    Interface
}

/// <summary>
/// Drives the badge shown next to a toggle and whether an extra confirmation is required.
/// </summary>
public enum RiskLevel
{
    /// <summary>Cosmetic or trivially reversible. No functional loss.</summary>
    Safe,

    /// <summary>Removes a feature you may actually use, but nothing breaks.</summary>
    Moderate,

    /// <summary>Measurably lowers the machine's security posture. Always warn explicitly.</summary>
    SecurityTradeoff,

    /// <summary>Will stop an unrelated feature from working at all (e.g. WSL, Hyper-V).</summary>
    Breaking
}

public enum TweakState
{
    Unknown,
    NotApplied,
    Applied,
    /// <summary>Some of the underlying values match the optimised state and some do not.</summary>
    Partial,
    Error
}

public enum ResultStatus
{
    Success,
    NoChangeNeeded,
    Blocked,
    Failed
}

public enum LogSeverity
{
    Trace,
    Info,
    Command,
    Success,
    Warning,
    Error
}
