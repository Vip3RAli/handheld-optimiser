namespace HandheldOptimiser.Models;

public enum AppxGroup
{
    ThirdPartyPreinstall,
    MicrosoftPromotional,
    Assistant,
    MediaAndUtilities
}

/// <summary>
/// One removable packaged app. <see cref="IdentityName"/> is the Appx identity name (not the display
/// name) because that is what Get-AppxPackage -Name matches on.
/// </summary>
public sealed class AppxTarget
{
    public required string IdentityName { get; init; }
    public required string FriendlyName { get; init; }
    public required AppxGroup Group { get; init; }

    /// <summary>Why someone might want to keep it. Shown in the UI so the choice is informed.</summary>
    public string? KeepIfNote { get; init; }

    /// <summary>
    /// Included by the "Select recommended" helper. Nothing is ever ticked automatically on load;
    /// this only drives that one button.
    /// </summary>
    public bool Recommended { get; init; }
}

/// <summary>
/// A catalog entry paired with what was actually found on this machine.
/// </summary>
public sealed class AppxPresence
{
    public required AppxTarget Target { get; init; }
    public bool InstalledForUser { get; set; }
    public bool Provisioned { get; set; }
    public string? FullPackageName { get; set; }

    public bool IsPresent => InstalledForUser || Provisioned;
}
