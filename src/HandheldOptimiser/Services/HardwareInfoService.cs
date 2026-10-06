using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.Services;

/// <summary>What the battery is doing right now. Capacities are in mWh and the rate in mW.</summary>
public sealed record BatteryReading(
    bool OnMains,
    bool Charging,
    uint FullChargedCapacity,
    uint RemainingCapacity,
    int? Rate)
{
    public int? ChargePercent => FullChargedCapacity == 0
        ? null
        : (int)Math.Round(Math.Min(100.0, RemainingCapacity * 100.0 / FullChargedCapacity));
}

/// <summary>The integrated graphics adapter and its driver.</summary>
public sealed record GraphicsReading(
    string Name,
    bool IsIntel,
    string DriverVersion,
    DateTime? DriverDate,
    long? DedicatedMemoryBytes,
    string? VendorSoftwareVersion);

/// <summary>
/// Read-only hardware facts for the Dashboard: battery charge, power draw and wear, and the graphics
/// adapter's memory and driver. Nothing here changes the system.
///
/// The battery is read through the power API and the battery driver directly rather than through
/// PowerShell: it is polled every couple of seconds, and the one tool that reports design capacity
/// reliably (powercfg /batteryreport) can only write it to a file, which an elevated app should not be
/// doing in a folder the user can write to.
/// </summary>
public sealed class HardwareInfoService(LogService log, PowerShellRunner runner)
{
    private const int SystemBatteryState = 5;
    private const int UnknownRate = unchecked((int)0x80000000);

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;

    /// <summary>Set when the battery counts in units of its own rather than mWh, so only ratios mean anything.</summary>
    private const uint CapacityRelative = 0x40000000;

    private static Guid _batteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    private readonly LogService _log = log;
    private readonly PowerShellRunner _runner = runner;

    private GraphicsReading? _graphics;
    private bool _graphicsLoaded;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryStateData
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public uint AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1;
        public byte Reserved2;
        public byte Reserved3;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, uint inputLength, out SystemBatteryStateData output, uint outputLength);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, ref uint input, int inputSize, out uint output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, ref BatteryQueryInformation input, int inputSize,
        out BatteryInformation output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// The battery's state this instant, or null when there is no battery. Cheap enough to call on a
    /// timer: one call into the power manager, which already has the figures.
    /// </summary>
    public BatteryReading? ReadBattery()
    {
        var status = CallNtPowerInformation(
            SystemBatteryState, IntPtr.Zero, 0, out var state, (uint)Marshal.SizeOf<SystemBatteryStateData>());

        if (status != 0 || state.BatteryPresent == 0)
        {
            return null;
        }

        // Batteries that cannot measure current report zero or the "unknown" marker, not a real rate.
        int? rate = state.Rate is 0 or UnknownRate ? null : Math.Abs(state.Rate);

        return new BatteryReading(
            OnMains: state.AcOnLine != 0,
            Charging: state.Charging != 0,
            FullChargedCapacity: state.MaxCapacity,
            RemainingCapacity: state.RemainingCapacity,
            Rate: rate);
    }

    /// <summary>
    /// The capacity the battery was built with, in mWh and summed over every battery, or null when no
    /// battery reports one in mWh. Asked of the battery driver itself, which answers without elevation.
    /// </summary>
    public uint? ReadDesignCapacity()
    {
        try
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, ref _batteryInterface, null, 0) != 0 || length <= 1)
            {
                return null;
            }

            var buffer = new char[length];
            if (CM_Get_Device_Interface_ListW(ref _batteryInterface, null, buffer, length, 0) != 0)
            {
                return null;
            }

            uint total = 0;

            foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                using var battery = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (battery.IsInvalid)
                {
                    continue;
                }

                // The tag names the battery currently in the slot; every other query has to quote it.
                uint wait = 0;
                if (!DeviceIoControl(battery, IoctlBatteryQueryTag, ref wait, sizeof(uint), out var tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
                {
                    continue;
                }

                var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = 0 };
                if (!DeviceIoControl(
                        battery, IoctlBatteryQueryInformation, ref query, Marshal.SizeOf<BatteryQueryInformation>(),
                        out var info, Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero))
                {
                    continue;
                }

                if ((info.Capabilities & CapacityRelative) != 0 || info.DesignedCapacity is 0 or uint.MaxValue)
                {
                    continue;
                }

                total += info.DesignedCapacity;
            }

            return total == 0 ? null : total;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Warning($"Could not read the battery's design capacity: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The AMD or Intel adapter Windows is using, with its dedicated memory and driver. Queried once and
    /// kept: none of it changes without a restart or a driver install. The memory figure comes from the
    /// driver's own registry key because Win32_VideoController's is a 32-bit number that stops at 4 GB.
    /// </summary>
    public async Task<GraphicsReading?> GetGraphicsAsync(CancellationToken ct = default)
    {
        if (_graphicsLoaded)
        {
            return _graphics;
        }

        var outcome = await _runner.RunScriptAsync(
            """
            $ErrorActionPreference = 'SilentlyContinue'
            $class = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\'
            foreach ($gpu in Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match '^PCI\\VEN_(1002|8086)' }) {
                $driverKey = (Get-PnpDeviceProperty -InstanceId $gpu.PNPDeviceID -KeyName DEVPKEY_Device_Driver).Data
                $props = if ($driverKey) { Get-ItemProperty ($class + $driverKey) } else { $null }
                $memory = $props.'HardwareInformation.qwMemorySize'
                if ($null -eq $memory) { $memory = $gpu.AdapterRAM }
                $date = if ($gpu.DriverDate) { $gpu.DriverDate.ToString('yyyy-MM-dd') } else { '' }
                $vendor = if ($gpu.PNPDeviceID -match 'VEN_8086') { 'intel' } else { 'amd' }
                Write-Output ("GPU|{0}|{1}|{2}|{3}|{4}|{5}" -f $vendor, $gpu.DriverVersion, $date, $memory, $props.RadeonSoftwareVersion, $gpu.Name)
            }
            """,
            "Query the graphics adapter and driver",
            ct,
            echoScript: false);

        // An external GPU can sit beside the built-in one. The built-in one is what the UMA buffer
        // belongs to, and it is the one with less dedicated memory.
        _graphics = outcome.OutputLines
            .Where(l => l.StartsWith("GPU|", StringComparison.Ordinal))
            .Select(ParseGraphics)
            .Where(g => g is not null)
            .MinBy(g => g!.DedicatedMemoryBytes ?? long.MaxValue);

        _graphicsLoaded = true;
        return _graphics;
    }

    private static GraphicsReading? ParseGraphics(string line)
    {
        // The name goes last and takes whatever is left, so a "|" in it cannot shift the other fields.
        var parts = line.Split('|', 7);
        if (parts.Length < 7)
        {
            return null;
        }

        return new GraphicsReading(
            Name: parts[6].Trim(),
            IsIntel: parts[1] == "intel",
            DriverVersion: parts[2].Trim(),
            DriverDate: DateTime.TryParseExact(parts[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            DedicatedMemoryBytes: long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : null,
            VendorSoftwareVersion: string.IsNullOrWhiteSpace(parts[5]) ? null : parts[5].Trim());
    }
}
