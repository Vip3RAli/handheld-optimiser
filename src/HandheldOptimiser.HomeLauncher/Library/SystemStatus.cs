using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the top bar shows beside the clock.
/// </summary>
/// <param name="BatteryPercent">Null on a machine with no battery.</param>
/// <param name="PluggedIn">On mains power, so charging or full.</param>
/// <param name="WifiQuality">0 to 100 when connected, 0 when not, null with no Wi-Fi adapter.</param>
internal sealed record SystemStatus(int? BatteryPercent, bool PluggedIn, int? WifiQuality)
{
    private const byte NoSystemBattery = 128;
    private const byte Unknown = 255;
    private const byte OnMains = 1;

    // WLAN_INTERFACE_INFO: a GUID, a 256 character description, then the interface state.
    private const int InterfaceListHeader = 8;
    private const int InterfaceInfoSize = 532;
    private const int InterfaceStateOffset = 528;

    // wlanSignalQuality inside WLAN_CONNECTION_ATTRIBUTES.wlanAssociationAttributes.
    private const int SignalQualityOffset = 576;

    /// <summary>Wi-Fi quality as the 0 to 4 bars shown in the top bar.</summary>
    public int WifiBars => WifiQuality switch
    {
        null or <= 0 => 0,
        < 25 => 1,
        < 50 => 2,
        < 75 => 3,
        _ => 4
    };

    public static SystemStatus Read()
    {
        int? battery = null;
        var pluggedIn = false;

        if (Native.GetSystemPowerStatus(out var power))
        {
            pluggedIn = power.AcLineStatus == OnMains;
            if (power.BatteryFlag != Unknown && (power.BatteryFlag & NoSystemBattery) == 0 && power.BatteryLifePercent <= 100)
            {
                battery = power.BatteryLifePercent;
            }
        }

        return new SystemStatus(battery, pluggedIn, ReadWifiQuality());
    }

    private static int? ReadWifiQuality()
    {
        nint handle = 0;
        nint list = 0;

        try
        {
            if (Native.WlanOpenHandle(Native.WlanClientVersion, 0, out _, out handle) != Native.ErrorSuccess
                || Native.WlanEnumInterfaces(handle, 0, out list) != Native.ErrorSuccess)
            {
                return null;
            }

            var count = Marshal.ReadInt32(list);
            if (count == 0)
            {
                return null;
            }

            var best = 0;
            for (var i = 0; i < count; i++)
            {
                var info = list + InterfaceListHeader + i * InterfaceInfoSize;
                if (Marshal.ReadInt32(info + InterfaceStateOffset) != Native.WlanInterfaceConnected)
                {
                    continue;
                }

                var id = Marshal.PtrToStructure<Guid>(info);
                if (Native.WlanQueryInterface(handle, in id, Native.WlanOpcodeCurrentConnection, 0, out var size, out var data, 0) != Native.ErrorSuccess)
                {
                    continue;
                }

                if (size >= SignalQualityOffset + sizeof(int))
                {
                    best = Math.Max(best, Math.Clamp(Marshal.ReadInt32(data + SignalQualityOffset), 1, 100));
                }

                Native.WlanFreeMemory(data);
            }

            return best;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows without the wireless service installed.
            return null;
        }
        finally
        {
            if (list != 0)
            {
                Native.WlanFreeMemory(list);
            }

            if (handle != 0)
            {
                Native.WlanCloseHandle(handle, 0);
            }
        }
    }
}
