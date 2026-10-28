namespace HandheldOptimiser.PowerService;

/// <summary>
/// Radeon Super Resolution and AMD Fluid Motion Frames, each on, off, or null where this device does not have it
/// or it could not be read. The launcher reads and sets them through AMD's ADLX in the player's session, as ADLX
/// crashes when asked about them from the service's session (tried on an Ally on 2026-10-28). The launcher says
/// what it found through its exit code, which TDP Engine reads. Shared with the launcher as a linked file.
/// </summary>
internal readonly record struct RadeonState(bool? Rsr, bool? Afmf)
{
    /// <summary>The launcher's argument: alone it reads, with a feature and on or off it sets one first.</summary>
    public const string Argument = "--radeon";

    public const string RsrName = "rsr";
    public const string AfmfName = "afmf";
    public const string OnName = "on";
    public const string OffName = "off";

    // The exit code is this marker with four bits under it, so a crash's code or a plain 0 or 1 is never read
    // as a state.
    private const int Marker = 0x40;
    private const int RsrKnown = 1;
    private const int RsrOn = 2;
    private const int AfmfKnown = 4;
    private const int AfmfOn = 8;

    /// <summary>The launcher's exit code saying this state.</summary>
    public int ToExitCode() =>
        Marker
        | (Rsr is null ? 0 : RsrKnown) | (Rsr == true ? RsrOn : 0)
        | (Afmf is null ? 0 : AfmfKnown) | (Afmf == true ? AfmfOn : 0);

    /// <summary>The state an exit code says, or null when it is not one of ours.</summary>
    public static RadeonState? FromExitCode(int code) =>
        (code & ~0xF) != Marker
            ? null
            : new RadeonState(
                (code & RsrKnown) != 0 ? (code & RsrOn) != 0 : null,
                (code & AfmfKnown) != 0 ? (code & AfmfOn) != 0 : null);
}
