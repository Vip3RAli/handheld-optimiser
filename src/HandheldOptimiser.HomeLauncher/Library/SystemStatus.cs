using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the top bar shows beside the clock.
/// </summary>
/// <param name="BatteryPercent">Null on a machine with no battery.</param>
/// <param name="PluggedIn">On mains power, so charging or full.</param>
/// <param name="WifiQuality">0 to 100 when connected, 0 when not, null with no Wi-Fi adapter.</param>
/// <param name="RemainingMinutes">Estimated minutes of battery playtime left while discharging, or null.</param>
internal sealed record SystemStatus(int? BatteryPercent, bool PluggedIn, int? WifiQuality, int? RemainingMinutes = null)
{
    private const byte NoSystemBattery = 128;
    private const byte Unknown = 255;
    private const byte OnMains = 1;
    private const int SystemBatteryState = 5;
    private const int UnknownRate = unchecked((int)0x80000000);

    /// <summary>The estimated battery runtime formatted as "1h 22m" or "45m", or null when plugged in or unknown.</summary>
    public string? RemainingTime => OverlayLayout.FormatDuration(RemainingMinutes);

    // WLAN_INTERFACE_INFO: a GUID, a 256 character description, then the interface state.
    private const int InterfaceListHeader = 8;
    private const int InterfaceInfoSize = 532;
    private const int InterfaceStateOffset = 528;

    // wlanSignalQuality inside WLAN_CONNECTION_ATTRIBUTES.wlanAssociationAttributes.
    private const int SignalQualityOffset = 576;

    // dot11Ssid at the start of wlanAssociationAttributes: its length in bytes, then up to 32 bytes of name.
    private const int SsidLengthOffset = 520;
    private const int SsidOffset = 524;
    private const int SsidMost = 32;

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
        int? remainingMinutes = null;

        if (Native.GetSystemPowerStatus(out var power))
        {
            pluggedIn = power.AcLineStatus == OnMains;
            if (power.BatteryFlag != Unknown && (power.BatteryFlag & NoSystemBattery) == 0 && power.BatteryLifePercent <= 100)
            {
                battery = power.BatteryLifePercent;
            }
        }

        if (battery is not null && !pluggedIn)
        {
            remainingMinutes = Native.ReadBatteryRemainingMinutes(UnknownRate, SystemBatteryState);
        }

        return new SystemStatus(battery, pluggedIn, ReadWifi().Quality, remainingMinutes);
    }

    /// <summary>
    /// The Wi-Fi connection with the best signal: its quality (0 to 100, 0 when not connected, null with no
    /// Wi-Fi adapter) and its network's name, which is null when not connected.
    /// </summary>
    public static (int? Quality, string? Name) ReadWifi()
    {
        string? name = null;
        nint handle = 0;
        nint list = 0;

        try
        {
            if (Native.WlanOpenHandle(Native.WlanClientVersion, 0, out _, out handle) != Native.ErrorSuccess
                || Native.WlanEnumInterfaces(handle, 0, out list) != Native.ErrorSuccess)
            {
                return (null, null);
            }

            var count = Marshal.ReadInt32(list);
            if (count == 0)
            {
                return (null, null);
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
                    var quality = Math.Clamp(Marshal.ReadInt32(data + SignalQualityOffset), 1, 100);
                    if (quality > best)
                    {
                        best = quality;

                        // A name is nearly always UTF-8, though nothing makes it so.
                        var length = Math.Clamp(Marshal.ReadInt32(data + SsidLengthOffset), 0, SsidMost);
                        var bytes = new byte[length];
                        Marshal.Copy(data + SsidOffset, bytes, 0, length);
                        name = length > 0 ? System.Text.Encoding.UTF8.GetString(bytes) : null;
                    }
                }

                Native.WlanFreeMemory(data);
            }

            return (best, name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows without the wireless service installed.
            return (null, null);
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
