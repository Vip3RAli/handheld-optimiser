using System.Runtime.InteropServices;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Whether a monitor is plugged in beside the handheld's own screen, from the monitors Windows has present.
/// Undocked there is one, the built-in panel; docked there are two or more. It asks the device manager, which
/// answers the same in the service's session as in the player's, so TDP Engine and the stats screen agree on
/// it: the service only starts the stats screen while docked, and the stats screen closes once undocked.
/// Shared with the launcher as a linked file.
/// </summary>
internal static partial class Monitors
{
    // GUID_DEVINTERFACE_MONITOR.
    private static readonly Guid MonitorInterface = new("e6f07b5f-ee97-4a90-b076-33f57bf4eaa7");

    private const uint PresentOnly = 0;
    private const uint Success = 0;
    private const uint BufferTooSmall = 0x1A;

    /// <summary>True while more than the built-in screen is present. False when Windows will not say.</summary>
    public static bool Docked() => IsDocked(Present());

    /// <summary>Docked is two or more monitors present.</summary>
    public static bool IsDocked(int present) => present >= 2;

    /// <summary>How many monitors Windows has present now, or 0 when it will not say.</summary>
    public static int Present()
    {
        // The list can grow between asking its size and reading it, as when a monitor is plugged in.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (ListSize(out var length, MonitorInterface, null, PresentOnly) != Success || length == 0)
            {
                return 0;
            }

            var buffer = new ushort[length];
            var result = List(MonitorInterface, null, buffer, length, PresentOnly);
            if (result == Success)
            {
                return CountNames(buffer);
            }

            if (result != BufferTooSmall)
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>The names in a list of UTF-16 strings, each ended by a zero, the whole ended by another.</summary>
    public static int CountNames(ReadOnlySpan<ushort> list)
    {
        var count = 0;
        var inName = false;
        foreach (var c in list)
        {
            if (c == 0)
            {
                if (!inName)
                {
                    break;
                }

                inName = false;
            }
            else if (!inName)
            {
                inName = true;
                count++;
            }
        }

        return count;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ListSize(out uint length, in Guid interfaceClass, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint List(in Guid interfaceClass, string? deviceId, [Out] ushort[] buffer, uint length, uint flags);
}
