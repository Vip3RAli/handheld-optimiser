using System.Runtime.InteropServices;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Radeon Super Resolution and AMD Fluid Motion Frames, read and switched through AMD's ADLX, which comes with
/// the graphics driver (amdadlx64.dll). Run by the launcher's --radeon mode, which TDP Engine starts in the
/// player's session for the widget: ADLX works there, and crashes when asked about these from the service's
/// own session. Both are global settings, the same switches as in Radeon Software.
///
/// ADLX is a C++ library with no .NET version, so its interfaces are called through their function tables.
/// The positions are from AMD's headers (ISystem.h, I3DSettings.h and I3DSettings1.h in the ADLX SDK) and were
/// checked against ADLX 1.5 on an Ally on 2026-10-28.
/// </summary>
internal static unsafe class RadeonSettings
{
    private const string Library = "amdadlx64.dll";

    // IADLXSystem has no Acquire or Release: Get3DSettingsServices is its eighth function.
    private const int SystemGet3DSettingsServices = 7;

    // Every other interface starts with Acquire, Release and QueryInterface.
    private const int ReleaseSlot = 1;
    private const int QueryInterfaceSlot = 2;
    private const int ServicesGetRadeonSuperResolution = 14;
    private const int Services1GetAmdFluidMotionFrames = 17;

    // RSR and AFMF share these three, straight after QueryInterface.
    private const int IsSupportedSlot = 3;
    private const int IsEnabledSlot = 4;
    private const int SetEnabledSlot = 5;

    private const string Services1Id = "IADLX3DSettingsServices1";

    // ADLX_OK, ADLX_ALREADY_ENABLED and ADLX_ALREADY_INITIALIZED all count as success.
    private static bool Succeeded(int result) => result is 0 or 1 or 2;

    /// <summary>
    /// Switches <paramref name="feature"/> (<see cref="RadeonState.RsrName"/> or <see cref="RadeonState.AfmfName"/>)
    /// on or off when one is named, then reads both. Null when ADLX could not be started.
    /// </summary>
    public static RadeonState? Apply(string? feature, bool on)
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
                    return new RadeonState(null, null);
                }

                try
                {
                    var rsr = WithFeature(Rsr(services), feature == RadeonState.RsrName, on);
                    var afmf = WithFeature(Afmf(services), feature == RadeonState.AfmfName, on);
                    return new RadeonState(rsr, afmf);
                }
                finally
                {
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

    private static nint Rsr(nint services)
    {
        nint rsr;
        return Succeeded(((delegate* unmanaged<nint, nint*, int>)Table(services)[ServicesGetRadeonSuperResolution])(services, &rsr)) ? rsr : 0;
    }

    /// <summary>AFMF is on a newer settings interface, asked for by name, which ADLX before 1.3 does not have.</summary>
    private static nint Afmf(nint services)
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
            nint afmf;
            return Succeeded(((delegate* unmanaged<nint, nint*, int>)Table(services1)[Services1GetAmdFluidMotionFrames])(services1, &afmf)) ? afmf : 0;
        }
        finally
        {
            Release(services1);
        }
    }

    /// <summary>
    /// The feature's state, switched first when <paramref name="change"/> says so. Null when this device does not
    /// have it. Releases the interface.
    /// </summary>
    private static bool? WithFeature(nint feature, bool change, bool on)
    {
        if (feature == 0)
        {
            return null;
        }

        try
        {
            byte supported = 0;
            if (!Succeeded(((delegate* unmanaged<nint, byte*, int>)Table(feature)[IsSupportedSlot])(feature, &supported)) || supported == 0)
            {
                return null;
            }

            if (change)
            {
                var result = ((delegate* unmanaged<nint, byte, int>)Table(feature)[SetEnabledSlot])(feature, on ? (byte)1 : (byte)0);
                if (!Succeeded(result))
                {
                    Program.Log($"Radeon settings: the driver would not switch it {(on ? "on" : "off")} (ADLX result {result})");
                }
            }

            byte enabled = 0;
            return Succeeded(((delegate* unmanaged<nint, byte*, int>)Table(feature)[IsEnabledSlot])(feature, &enabled)) ? enabled != 0 : null;
        }
        finally
        {
            Release(feature);
        }
    }

    private static nint* Table(nint instance) => *(nint**)instance;

    private static void Release(nint instance) =>
        ((delegate* unmanaged<nint, long>)Table(instance)[ReleaseSlot])(instance);
}
