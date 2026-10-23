namespace HandheldOptimiser.PowerService;

/// <summary>
/// How TDP Engine reaches one maker's firmware to set the processor's power limit and the fans. TDP Engine
/// talks only to this, so another handheld needs one more class and a line in <see cref="PowerBackends.Open"/>,
/// not changes through the service.
///
/// Every maker keeps its own idea of the power setting the player chose (Armoury Crate's operating mode on
/// the Ally), and setting it again brings back that setting's own limits and fan curve. TDP Engine reads it
/// as a game starts and puts it back as the game ends, and it keeps it in the registry meanwhile, so it is a
/// plain number.
/// </summary>
internal interface IPowerBackend : IDisposable
{
    /// <summary>A short name for the log, such as "ASUS ACPI".</summary>
    string Name { get; }

    /// <summary>Whether this model takes power limits this way.</summary>
    bool CanSetPowerLimits { get; }

    /// <summary>The processor's temperature in °C, or null when the firmware does not report it.</summary>
    int? ReadTemperature();

    /// <summary>Sets the power limit, holding it rather than boosting past it.</summary>
    /// <returns>False when the firmware refused it.</returns>
    bool SetPowerLimit(int watts);

    /// <summary>Sets the fans to a curve from <see cref="FanCurves"/>: eight temperatures, then eight speeds.</summary>
    /// <returns>False when the firmware refused it, as a model without custom fan curves does.</returns>
    bool SetFanCurve(byte[] curve);

    /// <summary>The maker's power setting now, or null when it cannot be read.</summary>
    int? ReadVendorMode();

    /// <summary>The setting to put back when the one in use could not be read: the maker's own default.</summary>
    int DefaultVendorMode { get; }

    /// <summary>Sets the maker's power setting, which also puts back its own power limits and fan curve.</summary>
    /// <returns>False when the firmware refused it.</returns>
    bool SetVendorMode(int mode);
}

/// <summary>Finds the power backend for the handheld this runs on.</summary>
internal static class PowerBackends
{
    /// <summary>
    /// The first backend that opens and can set power limits, or null when none can. Only ASUS's is known
    /// today. Others (Lenovo's WMI on the Legion Go, say) go in the list as they are written and tried on
    /// their own hardware.
    /// </summary>
    public static IPowerBackend? Open()
    {
        Func<IPowerBackend?>[] candidates = [AsusAcpi.Open];
        foreach (var open in candidates)
        {
            if (open() is not { } backend)
            {
                continue;
            }

            if (backend.CanSetPowerLimits)
            {
                return backend;
            }

            Log.Write($"{backend.Name} opened but does not take power limits on this model");
            backend.Dispose();
        }

        return null;
    }
}
