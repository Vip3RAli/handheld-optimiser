namespace HandheldOptimiser.PowerService;

/// <summary>Windows' processor boost mode on the active power plan, plugged in and on battery.</summary>
internal readonly record struct BoostModes(uint PluggedIn, uint OnBattery);

/// <summary>What CpuBoost writes, with no Windows calls, so it can be tested.</summary>
internal static class CpuBoostRules
{
    public const uint Disabled = 0;
    public const uint Aggressive = 2;
    public const uint EfficientEnabled = 3;
    public const uint EfficientAggressive = 4;

    /// <summary>The modes to write for boost on or off, given the modes saved from before it was turned off.</summary>
    public static BoostModes Target(bool on, BoostModes saved) => on
        ? new BoostModes(Restore(saved.PluggedIn), Restore(saved.OnBattery))
        : new BoostModes(Disabled, Disabled);

    /// <summary>The boost modes for an explicit mode name ("off", "efficient", or "aggressive").</summary>
    public static BoostModes TargetForMode(string? mode, BoostModes saved) => mode switch
    {
        AutoTdpProtocol.BoostOff => new BoostModes(Disabled, Disabled),
        AutoTdpProtocol.BoostEfficient => new BoostModes(EfficientEnabled, EfficientEnabled),
        AutoTdpProtocol.BoostAggressive => new BoostModes(Aggressive, Aggressive),
        _ => saved
    };

    /// <summary>Maps raw Windows boost indices on the active power source to "off", "efficient", or "aggressive".</summary>
    public static string ModeName(BoostModes modes, bool onBattery) =>
        (onBattery ? modes.OnBattery : modes.PluggedIn) switch
        {
            Disabled => AutoTdpProtocol.BoostOff,
            EfficientEnabled or EfficientAggressive or 6 => AutoTdpProtocol.BoostEfficient,
            _ => AutoTdpProtocol.BoostAggressive
        };

    /// <summary>
    /// What to save before turning boost off: the modes as they are, unless both are already Disabled, in
    /// which case what was saved before still says what On should bring back.
    /// </summary>
    public static BoostModes ToSave(BoostModes current, BoostModes saved) =>
        current.PluggedIn == Disabled && current.OnBattery == Disabled ? saved : current;

    /// <summary>Whether boost is on for the power source in use.</summary>
    public static bool IsOn(BoostModes modes, bool onBattery) =>
        (onBattery ? modes.OnBattery : modes.PluggedIn) != Disabled;

    private static uint Restore(uint saved) => saved == Disabled ? Aggressive : saved;
}
