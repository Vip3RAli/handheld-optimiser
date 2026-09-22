namespace HandheldOptimiser.Models;

public enum RuntimeGroup
{
    Essential,
    Legacy
}

public enum RuntimeDetection
{
    /// <summary>Installed and available versions come from <c>winget list</c>.</summary>
    Winget,

    /// <summary>Present if every file in <see cref="RuntimePackage.DetectFiles"/> exists.</summary>
    Files,

    /// <summary>.NET Framework 3.5, a Windows optional feature rather than a package.</summary>
    NetFx3,

    /// <summary>
    /// Modern .NET. Installed versions are read from the shared framework folders, because winget's
    /// matching of installed .NET entries to package IDs is unreliable (it files some under ".x64" IDs
    /// and reports leftover older patches as outdated). Only the latest version comes from winget.
    /// </summary>
    DotNet
}

public enum RuntimeStatus
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    Missing,
    Error
}

/// <summary>
/// A shared runtime games depend on. Installs only ever go through the IDs in this catalog, never a
/// free-form package name, so the page cannot be used to install arbitrary software.
/// </summary>
public sealed class RuntimePackage
{
    /// <summary>winget package ID, or an internal ID for runtimes installed another way.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public RuntimeGroup Group { get; init; } = RuntimeGroup.Essential;
    public RuntimeDetection Detection { get; init; } = RuntimeDetection.Winget;

    /// <summary>
    /// Whether "Install missing essentials" should add this when it is absent. False for runtimes that
    /// are only worth updating: nothing needs .NET 6 unless an app already installed it.
    /// </summary>
    public bool InstallIfMissing { get; init; } = true;

    /// <summary>Files checked by <see cref="RuntimeDetection.Files"/>, with environment variables.</summary>
    public IReadOnlyList<string> DetectFiles { get; init; } = [];

    /// <summary>Shown as the version for runtimes that have no version number of their own.</summary>
    public string? FixedVersionLabel { get; init; }

    /// <summary>For <see cref="RuntimeDetection.DotNet"/>: the folder under dotnet\shared, e.g. Microsoft.NETCore.App.</summary>
    public string? DotNetFramework { get; init; }

    /// <summary>For <see cref="RuntimeDetection.DotNet"/>: the major version, e.g. "8".</summary>
    public string? DotNetMajor { get; init; }
}
