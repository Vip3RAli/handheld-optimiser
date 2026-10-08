namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Windows' power mode, as in Settings, System, Power and battery.</summary>
internal enum PowerMode
{
    Efficiency,
    Balanced,
    Performance
}

/// <summary>
/// Reads and switches Windows' power mode, which is how Windows itself trades battery for speed. It does
/// not set the processor's wattage the way Armoury Crate's operating modes do; that needs ASUS's driver.
/// Switching needs no administrator rights: it is the call the Settings app makes.
/// </summary>
internal static class PowerModes
{
    // Windows only offers power modes while the Balanced plan is active.
    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private static readonly Guid EfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid PerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static string Name(PowerMode mode) => mode switch
    {
        PowerMode.Efficiency => "Best power efficiency",
        PowerMode.Performance => "Best performance",
        _ => "Balanced"
    };

    /// <summary>Whether the active power plan is Balanced, the only one power modes work with.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return Native.ActivePowerPlan() == BalancedPlan;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>The current mode, or null when power modes are not available or one Windows did not name is set.</summary>
    public static PowerMode? Read()
    {
        try
        {
            if (!Available || Native.PowerGetEffectiveOverlayScheme(out var overlay) != Native.ErrorSuccess)
            {
                return null;
            }

            return overlay == EfficiencyOverlay ? PowerMode.Efficiency
                : overlay == PerformanceOverlay ? PowerMode.Performance
                : overlay == Guid.Empty ? PowerMode.Balanced
                : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Windows before power modes existed.
            return null;
        }
    }

    /// <returns>False when power modes are not available or Windows refused.</returns>
    public static bool Set(PowerMode mode)
    {
        var overlay = mode switch
        {
            PowerMode.Efficiency => EfficiencyOverlay,
            PowerMode.Performance => PerformanceOverlay,
            _ => Guid.Empty
        };

        try
        {
            return Available && Native.PowerSetActiveOverlayScheme(overlay) == Native.ErrorSuccess;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }
}
