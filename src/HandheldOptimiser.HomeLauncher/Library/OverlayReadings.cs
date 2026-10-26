using System.Diagnostics;
using System.Runtime.InteropServices;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// What the overlay reads for itself each second, in the player's session: processor and graphics use,
/// memory and the battery, the processor's clock and the power drawn, and which program is in front. The frame rate, power limit and temperature come
/// from TDP Engine, which can read what this cannot.
/// </summary>
internal sealed partial class OverlayReadings : IDisposable
{
    // The 3D engines' use, summed over every program, as Task Manager's GPU figure is.
    private const string GpuCounter = @"\GPU Engine(*engtype_3D)\Utilization Percentage";

    // The processor's clock, as Task Manager's speed figure: its base clock in MHz, times how far above or
    // below it the processors run, in percent.
    private const string BaseClockCounter = @"\Processor Information(_Total)\Processor Frequency";
    private const string PerformanceCounter = @"\Processor Information(_Total)\% Processor Performance";

    // The power the whole chip draws, processor and graphics, in milliwatts, from Windows' energy meter.
    private const string PackagePowerCounter = @"\Energy Meter(RAPL_Package0_PKG)\Power";

    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    private long _idle;
    private long _busy;
    private nint _query;
    private nint _gpu;
    private nint _baseClock;
    private nint _performance;
    private nint _packagePower;

    public OverlayReadings()
    {
        SampleCpu();

        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            Program.Log("Overlay: Windows would not give its performance counters");
            _query = 0;
            return;
        }

        _gpu = Add(GpuCounter, "graphics use");
        _baseClock = Add(BaseClockCounter, "processor clock");
        _performance = Add(PerformanceCounter, "processor clock");
        _packagePower = Add(PackagePowerCounter, "power drawn");

        // Use and clock are rates, so they need a first sample to measure from.
        PdhCollectQueryData(_query);
    }

    /// <summary>This second's readings, with TDP Engine's figures from <paramref name="status"/>.</summary>
    public OverlayReading Read(AutoTdpStatus status)
    {
        var collected = _query != 0 && PdhCollectQueryData(_query) == 0;
        var (memoryUsed, memoryTotal) = Memory();
        var (battery, charging) = Battery();
        return new OverlayReading(status.Fps, status.Watts, status.Temperature, SampleCpu(), collected ? Gpu() : null,
            memoryUsed, memoryTotal, battery, charging,
            CpuGhz: collected ? CpuGhz() : null,
            PowerDrawn: collected && Value(_packagePower) is { } milliwatts ? milliwatts / 1000 : null);
    }

    /// <returns>The counter, or 0 when this device does not have it, which is logged once.</returns>
    private nint Add(string path, string what)
    {
        if (PdhAddEnglishCounterW(_query, path, 0, out var counter) == 0)
        {
            return counter;
        }

        Program.Log($"Overlay: Windows would not give the {what} counter ({path})");
        return 0;
    }

    private double? CpuGhz() =>
        Value(_baseClock) is { } baseMhz and > 0 && Value(_performance) is { } percent and > 0 ? baseMhz * percent / 100 / 1000 : null;

    /// <summary>A single counter's value now, or null when it is not there or has no value yet.</summary>
    private static double? Value(nint counter)
    {
        if (counter == 0 || PdhGetFormattedCounterValue(counter, PdhFormatDouble, out _, out var value) != 0 || value.Status != 0)
        {
            return null;
        }

        return value.Value;
    }

    /// <summary>
    /// The program in front: the owner of the foreground window, or, for a Store game drawn inside
    /// ApplicationFrameHost's frame, the game inside it, whose frames are the ones counted.
    /// </summary>
    public static int ForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(window, out var owner);
        if (!IsFrameHost((int)owner))
        {
            return (int)owner;
        }

        var inner = (int)owner;
        EnumChildWindows(window, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var id);
            if (id != owner)
            {
                inner = (int)id;
                return false;
            }

            return true;
        }, 0);

        return inner;
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    private static bool IsFrameHost(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Processor use since the last call, 0 to 100, from the time every processor spent idle.</summary>
    private double? SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time includes idle time.
        var busy = kernel + user - idle;
        var idleDelta = idle - _idle;
        var busyDelta = busy - _busy;
        _idle = idle;
        _busy = busy;

        var total = idleDelta + busyDelta;
        return total > 0 ? 100.0 * busyDelta / total : null;
    }

    private unsafe double? Gpu()
    {
        if (_gpu == 0)
        {
            return null;
        }

        uint size = 0;
        if (PdhGetFormattedCounterArrayW(_gpu, PdhFormatDouble, ref size, out _, null) != PdhMoreData || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        fixed (byte* start = buffer)
        {
            if (PdhGetFormattedCounterArrayW(_gpu, PdhFormatDouble, ref size, out var count, start) != 0)
            {
                return null;
            }

            var items = (CounterItem*)start;
            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                // A status other than 0 is an engine that went away between the samples.
                if (items[i].Status == 0)
                {
                    sum += items[i].Value;
                }
            }

            return Math.Min(sum, 100);
        }
    }

    private static (double? Used, double? Total) Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (null, null);
        }

        const double Gb = 1024.0 * 1024 * 1024;
        return ((status.TotalPhys - status.AvailPhys) / Gb, status.TotalPhys / Gb);
    }

    private static (int? Percent, bool Charging) Battery()
    {
        // BatteryFlag 128 is no battery, and a percentage of 255 is not known.
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == 128 || status.BatteryLifePercent == 255)
        {
            return (null, false);
        }

        return (status.BatteryLifePercent, status.AcLineStatus == 1);
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: the instance name, then PDH_FMT_COUNTERVALUE (a status and a double).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem
    {
        [FieldOffset(0)] public nint Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public double Value;
    }

    // PDH_FMT_COUNTERVALUE: a status, then the value, as a double here.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsCallback callback, nint parameter);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
