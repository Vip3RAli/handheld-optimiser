using System.Runtime.InteropServices;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The AMD Radeon settings in the widget (see <see cref="RadeonState"/>), read and set through AMD's ADLX, which
/// comes with the graphics driver (amdadlx64.dll). Run by the launcher's --radeon mode, which TDP Engine starts
/// in the player's session for the widget: ADLX works there, and crashes when asked about these from the
/// service's own session. All are global settings, the same as in Radeon Software.
///
/// ADLX is a C++ library with no .NET version, so its interfaces are called through their function tables.
/// The positions are from AMD's headers (ISystem.h, ICollections.h, I3DSettings.h and I3DSettings1.h in the ADLX
/// SDK). RSR and AFMF were checked against ADLX 1.5 on an Ally on 2026-10-28.
/// </summary>
internal static unsafe class RadeonSettings
{
    private const string Library = "amdadlx64.dll";

    // IADLXSystem has no Acquire or Release.
    private const int SystemGetGpus = 1;
    private const int SystemGet3DSettingsServices = 7;

    // Every other interface starts with Acquire, Release and QueryInterface.
    private const int ReleaseSlot = 1;
    private const int QueryInterfaceSlot = 2;

    // IADLXGPUList: IADLXList's Size, Empty, Begin, End, At, Clear, Remove_Back and Add_Back, then its own At.
    private const int ListBegin = 5;
    private const int GpuListAt = 11;

    // IADLX3DSettingsServices. Anti-Lag, Chill and Image Sharpening are asked for per graphics card.
    private const int ServicesGetAntiLag = 3;
    private const int ServicesGetChill = 4;
    private const int ServicesGetImageSharpening = 6;
    private const int ServicesGetRadeonSuperResolution = 14;
    private const int Services1GetAmdFluidMotionFrames = 17;

    private const string Services1Id = "IADLX3DSettingsServices1";

    // Every setting here starts with IsSupported and IsEnabled; where SetEnabled sits differs.
    private const int IsSupportedSlot = 3;
    private const int IsEnabledSlot = 4;

    // IADLX3DRadeonSuperResolution, IADLX3DAMDFluidMotionFrames and IADLX3DAntiLag: SetEnabled straight after.
    private const int PlainSetEnabled = 5;
    private const int RsrGetSharpnessRange = 6;
    private const int RsrGetSharpness = 7;
    private const int RsrSetSharpness = 8;

    // IADLX3DChill: GetFPSRange, GetMinFPS, GetMaxFPS, SetEnabled, SetMinFPS, SetMaxFPS.
    private const int ChillGetRange = 5;
    private const int ChillGetMin = 6;
    private const int ChillGetMax = 7;
    private const int ChillSetEnabled = 8;
    private const int ChillSetMin = 9;
    private const int ChillSetMax = 10;

    // IADLX3DImageSharpening: GetSharpnessRange, GetSharpness, SetEnabled, SetSharpness.
    private const int SharpeningGetRange = 5;
    private const int SharpeningGet = 6;
    private const int SharpeningSetEnabled = 7;
    private const int SharpeningSet = 8;

    // ADLX_OK, ADLX_ALREADY_ENABLED and ADLX_ALREADY_INITIALIZED all count as success.
    private static bool Succeeded(int result) => result is 0 or 1 or 2;

    /// <summary>One change asked for: a setting's name and on or off, or a number.</summary>
    internal readonly record struct Change(string Name, bool? On, int? Value);

    /// <summary>
    /// Makes <paramref name="change"/> when there is one, then reads every setting. Null when ADLX could not be
    /// started.
    /// </summary>
    public static RadeonState? Apply(Change? change)
    {
        nint dll = 0;
        try
        {
            if (!NativeLibrary.TryLoad(Library, out dll))
            {
                Program.Log("Radeon settings: AMD's ADLX is not installed");
                return null;
            }

            var queryVersion = (delegate* unmanaged<ulong*, int>)NativeLibrary.GetExport(dll, "ADLXQueryFullVersion");
            var initialize = (delegate* unmanaged<ulong, nint*, int>)NativeLibrary.GetExport(dll, "ADLXInitialize");
            var terminate = (delegate* unmanaged<int>)NativeLibrary.GetExport(dll, "ADLXTerminate");

            ulong version;
            nint system;
            if (!Succeeded(queryVersion(&version)) || !Succeeded(initialize(version, &system)) || system == 0)
            {
                Program.Log("Radeon settings: ADLX would not start");
                return null;
            }

            try
            {
                nint services;
                if (!Succeeded(((delegate* unmanaged<nint, nint*, int>)Table(system)[SystemGet3DSettingsServices])(system, &services)) || services == 0)
                {
                    return RadeonState.Unknown;
                }

                var gpu = FirstGpu(system);
                try
                {
                    var state = RadeonState.Unknown;
                    state = Rsr(state, Get(services, ServicesGetRadeonSuperResolution), change);
                    state = Afmf(state, AfmfOf(services), change);
                    if (gpu != 0)
                    {
                        state = AntiLag(state, Get(services, ServicesGetAntiLag, gpu), change);
                        state = Chill(state, Get(services, ServicesGetChill, gpu), change);
                        state = Sharpening(state, Get(services, ServicesGetImageSharpening, gpu), change);
                    }

                    return state;
                }
                finally
                {
                    if (gpu != 0)
                    {
                        Release(gpu);
                    }

                    Release(services);
                }
            }
            finally
            {
                terminate();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Program.Log($"Radeon settings: ADLX could not be used: {ex.Message}");
            return null;
        }
        finally
        {
            if (dll != 0)
            {
                NativeLibrary.Free(dll);
            }
        }
    }

    private static RadeonState Rsr(RadeonState state, nint rsr, Change? change)
    {
        if (!Supported(rsr))
        {
            return state;
        }

        try
        {
            var range = Range(rsr, RsrGetSharpnessRange);
            SetSwitch(rsr, PlainSetEnabled, change, RadeonState.RsrName);
            SetNumber(rsr, RsrSetSharpness, range, change, RadeonState.RsrSharpnessName);
            return state with
            {
                Rsr = Enabled(rsr),
                RsrSharpness = Number(rsr, RsrGetSharpness),
                RsrSharpnessRange = range,
            };
        }
        finally
        {
            Release(rsr);
        }
    }

    private static RadeonState Afmf(RadeonState state, nint afmf, Change? change)
    {
        if (!Supported(afmf))
        {
            return state;
        }

        try
        {
            SetSwitch(afmf, PlainSetEnabled, change, RadeonState.AfmfName);
            return state with { Afmf = Enabled(afmf) };
        }
        finally
        {
            Release(afmf);
        }
    }

    private static RadeonState AntiLag(RadeonState state, nint antiLag, Change? change)
    {
        if (!Supported(antiLag))
        {
            return state;
        }

        try
        {
            SetSwitch(antiLag, PlainSetEnabled, change, RadeonState.AntiLagName);
            return state with { AntiLag = Enabled(antiLag) };
        }
        finally
        {
            Release(antiLag);
        }
    }

    /// <summary>
    /// Chill's lowest frame rate can never be above its highest, so a change that would cross the other moves the
    /// other first, as Radeon Software's two sliders do.
    /// </summary>
    private static RadeonState Chill(RadeonState state, nint chill, Change? change)
    {
        if (!Supported(chill))
        {
            return state;
        }

        try
        {
            var range = Range(chill, ChillGetRange);
            SetSwitch(chill, ChillSetEnabled, change, RadeonState.ChillName);
            if (change is { Value: { } asked } ask && range is { } fps)
            {
                var value = fps.Fit(asked);
                if (ask.Name == RadeonState.ChillMinName)
                {
                    if (Number(chill, ChillGetMax) < value)
                    {
                        Set(chill, ChillSetMax, value, "Chill's highest frame rate");
                    }

                    Set(chill, ChillSetMin, value, "Chill's lowest frame rate");
                }
                else if (ask.Name == RadeonState.ChillMaxName)
                {
                    if (Number(chill, ChillGetMin) > value)
                    {
                        Set(chill, ChillSetMin, value, "Chill's lowest frame rate");
                    }

                    Set(chill, ChillSetMax, value, "Chill's highest frame rate");
                }
            }

            return state with
            {
                Chill = Enabled(chill),
                ChillMin = Number(chill, ChillGetMin),
                ChillMax = Number(chill, ChillGetMax),
                ChillRange = range,
            };
        }
        finally
        {
            Release(chill);
        }
    }

    private static RadeonState Sharpening(RadeonState state, nint sharpening, Change? change)
    {
        if (!Supported(sharpening))
        {
            return state;
        }

        try
        {
            var range = Range(sharpening, SharpeningGetRange);
            SetSwitch(sharpening, SharpeningSetEnabled, change, RadeonState.SharpeningName);
            SetNumber(sharpening, SharpeningSet, range, change, RadeonState.SharpeningStrengthName);
            return state with
            {
                Sharpening = Enabled(sharpening),
                SharpeningStrength = Number(sharpening, SharpeningGet),
                SharpeningRange = range,
            };
        }
        finally
        {
            Release(sharpening);
        }
    }

    /// <summary>The first graphics card ADLX lists, or 0. A handheld has one.</summary>
    private static nint FirstGpu(nint system)
    {
        nint list;
        if (!Succeeded(((delegate* unmanaged<nint, nint*, int>)Table(system)[SystemGetGpus])(system, &list)) || list == 0)
        {
            return 0;
        }

        try
        {
            var first = ((delegate* unmanaged<nint, uint>)Table(list)[ListBegin])(list);
            nint gpu;
            return Succeeded(((delegate* unmanaged<nint, uint, nint*, int>)Table(list)[GpuListAt])(list, first, &gpu)) ? gpu : 0;
        }
        finally
        {
            Release(list);
        }
    }

    /// <summary>A setting the services hand out with no graphics card named, or 0.</summary>
    private static nint Get(nint services, int slot)
    {
        nint feature;
        return Succeeded(((delegate* unmanaged<nint, nint*, int>)Table(services)[slot])(services, &feature)) ? feature : 0;
    }

    /// <summary>A setting the services hand out for <paramref name="gpu"/>, or 0.</summary>
    private static nint Get(nint services, int slot, nint gpu)
    {
        nint feature;
        return Succeeded(((delegate* unmanaged<nint, nint, nint*, int>)Table(services)[slot])(services, gpu, &feature)) ? feature : 0;
    }

    /// <summary>AFMF is on a newer settings interface, asked for by name, which ADLX before 1.3 does not have.</summary>
    private static nint AfmfOf(nint services)
    {
        nint services1;
        fixed (char* id = Services1Id)
        {
            if (!Succeeded(((delegate* unmanaged<nint, char*, nint*, int>)Table(services)[QueryInterfaceSlot])(services, id, &services1)) || services1 == 0)
            {
                return 0;
            }
        }

        try
        {
            return Get(services1, Services1GetAmdFluidMotionFrames);
        }
        finally
        {
            Release(services1);
        }
    }

    /// <summary>Whether the driver offers this setting here. Releases it when not, so the caller only releases what is supported.</summary>
    private static bool Supported(nint feature)
    {
        if (feature == 0)
        {
            return false;
        }

        byte supported = 0;
        if (Succeeded(((delegate* unmanaged<nint, byte*, int>)Table(feature)[IsSupportedSlot])(feature, &supported)) && supported != 0)
        {
            return true;
        }

        Release(feature);
        return false;
    }

    private static bool? Enabled(nint feature)
    {
        byte enabled = 0;
        return Succeeded(((delegate* unmanaged<nint, byte*, int>)Table(feature)[IsEnabledSlot])(feature, &enabled)) ? enabled != 0 : null;
    }

    private static int? Number(nint feature, int slot)
    {
        int value;
        return Succeeded(((delegate* unmanaged<nint, int*, int>)Table(feature)[slot])(feature, &value)) ? value : null;
    }

    private static RadeonRange? Range(nint feature, int slot)
    {
        // ADLX_IntRange: minValue, maxValue, step.
        var range = stackalloc int[3];
        return Succeeded(((delegate* unmanaged<nint, int*, int>)Table(feature)[slot])(feature, range)) && range[0] <= range[1]
            ? new RadeonRange(range[0], range[1], range[2])
            : null;
    }

    /// <summary>Switches the setting on or off when <paramref name="change"/> is for it.</summary>
    private static void SetSwitch(nint feature, int slot, Change? change, string name)
    {
        if (change is not { On: { } on } ask || ask.Name != name)
        {
            return;
        }

        var result = ((delegate* unmanaged<nint, byte, int>)Table(feature)[slot])(feature, on ? (byte)1 : (byte)0);
        if (!Succeeded(result))
        {
            Program.Log($"Radeon settings: the driver would not switch {name} {(on ? "on" : "off")} (ADLX result {result})");
        }
    }

    /// <summary>Sets the setting's number, fitted to its range, when <paramref name="change"/> is for it.</summary>
    private static void SetNumber(nint feature, int slot, RadeonRange? range, Change? change, string name)
    {
        if (change is not { Value: { } value } ask || ask.Name != name || range is not { } fits)
        {
            return;
        }

        Set(feature, slot, fits.Fit(value), name);
    }

    private static void Set(nint feature, int slot, int value, string what)
    {
        var result = ((delegate* unmanaged<nint, int, int>)Table(feature)[slot])(feature, value);
        if (!Succeeded(result))
        {
            Program.Log($"Radeon settings: the driver would not set {what} to {value} (ADLX result {result})");
        }
    }

    private static nint* Table(nint instance) => *(nint**)instance;

    private static void Release(nint instance) =>
        ((delegate* unmanaged<nint, long>)Table(instance)[ReleaseSlot])(instance);
}
