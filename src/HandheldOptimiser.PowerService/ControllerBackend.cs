namespace HandheldOptimiser.PowerService;

/// <summary>
/// A handheld's built-in controller, as <see cref="AllyControlsHost"/> uses it: its front panel buttons in,
/// and its mode out. The host talks only to this, so another handheld's controller needs one more class and
/// a line in <see cref="ControllerBackends.Open"/>.
///
/// The buttons keep the Ally's names, as the Ally is the only one so far: <see cref="AllyButton.ArmouryCrate"/>
/// is the button that opens the library, and <see cref="AllyButton.CommandCenter"/> the one that opens Game Bar.
/// </summary>
internal interface IControllerBackend : IDisposable
{
    /// <summary>What was found, for the log.</summary>
    string Describe();

    /// <summary>Gets the controller ready for its mode to be set, as it needs after sleep or a reconnect.</summary>
    /// <returns>False when it did not answer.</returns>
    bool Wake();

    /// <summary>Puts the controller in a mode.</summary>
    /// <returns>False when it did not answer, which means it has gone.</returns>
    bool SetMode(ControllerMode mode);

    /// <summary>
    /// Waits for the controller's next report. <paramref name="button"/> is <see cref="AllyButton.None"/> for a
    /// report that is not about a front panel button.
    /// </summary>
    /// <returns>False once the controller has gone, or once this is disposed.</returns>
    bool TryRead(out AllyButton button);
}

/// <summary>Finds the controller of the handheld this runs on.</summary>
internal static class ControllerBackends
{
    /// <summary>The first controller found, or null when there is none this service knows.</summary>
    public static IControllerBackend? Open() => AllyHid.Open();
}
