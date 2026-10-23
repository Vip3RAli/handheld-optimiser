using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// ASUS's ACPI interface, the one Armoury Crate and G-Helper use to set the processor's power limits and
/// the fan curves. It
/// is a device ASUS's own driver (ATKACPI) puts there; nothing is installed for it. The method and device
/// numbers are the ones G-Helper (github.com/seerge/g-helper, app/AsusACPI.cs) uses on the ROG Ally.
/// </summary>
internal sealed partial class AsusAcpi : IPowerBackend
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;

    // The two methods: read a device's status, and set it.
    private const uint Dsts = 0x53545344;
    private const uint Devs = 0x53564544;

    // The power limits. On the Ally the sustained limit (SPL), the slow boost limit (sPPT) and the fast
    // boost limit (fPPT) are all set to the same figure, so the limit holds rather than boosting past it.
    private const uint SustainedLimit = 0x001200A3;
    private const uint SlowLimit = 0x001200A0;
    private const uint FastLimit = 0x001200C1;

    private const uint CpuTemperature = 0x00120094;

    /// <summary>Armoury Crate's operating mode: 0 Performance, 1 Turbo, 2 Silent.</summary>
    private const uint OperatingMode = 0x00120075;

    private const int PerformanceMode = 0;

    // The fan curves, one for each of the Ally's two fans, each 16 bytes: eight temperatures then eight speeds.
    private const uint CpuFanCurve = 0x00110024;
    private const uint GpuFanCurve = 0x00110025;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _device;
    private readonly bool _fastLimit;

    private AsusAcpi(SafeFileHandle device)
    {
        _device = device;
        _fastLimit = Get(FastLimit) >= 0;
    }

    /// <summary>The interface, or null on a device without it, which is any handheld but an ASUS one.</summary>
    public static AsusAcpi? Open()
    {
        var device = CreateFile(DevicePath, GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (device.IsInvalid)
        {
            device.Dispose();
            return null;
        }

        return new AsusAcpi(device);
    }

    /// <inheritdoc/>
    public string Name => "ASUS ACPI";

    /// <inheritdoc/>
    public bool CanSetPowerLimits => Get(SustainedLimit) >= 0 && Get(SlowLimit) >= 0;

    /// <inheritdoc/>
    /// <remarks>The sustained, slow boost and fast boost limits are all set to it.</remarks>
    public bool SetPowerLimit(int watts)
    {
        var sustained = Set(SustainedLimit, watts);
        var slow = Set(SlowLimit, watts);
        if (_fastLimit)
        {
            Set(FastLimit, watts);
        }

        return sustained == 1 && slow == 1;
    }

    /// <inheritdoc/>
    public int? ReadTemperature() => Get(CpuTemperature) is var degrees and > 0 and < 120 ? degrees : null;

    /// <summary>Armoury Crate's operating mode, or null when it cannot be read.</summary>
    /// <remarks>
    /// Anything outside 0 to 2 reads as null, so Performance is put back afterwards. Armoury Crate's Manual
    /// mode may read as another number, but whether the firmware takes that number back through the same
    /// call is not known (G-Helper sets Manual's limits itself on top of Performance), so it is not kept.
    /// </remarks>
    public int? ReadVendorMode() => Get(OperatingMode) is var mode and >= 0 and <= 2 ? mode : null;

    /// <summary>Performance, Armoury Crate's own default.</summary>
    public int DefaultVendorMode => PerformanceMode;

    /// <summary>Sets an operating mode, which also puts back that mode's own power limits and fan curve.</summary>
    public bool SetVendorMode(int mode) => Set(OperatingMode, mode) == 1;

    /// <summary>
    /// Sets both fans to a curve, as G-Helper does. It holds until the operating mode is set again, which
    /// puts back that mode's own curve.
    /// </summary>
    /// <returns>False when the firmware refused it, as a model without custom fan curves does.</returns>
    public bool SetFanCurve(byte[] curve)
    {
        if (curve.Length != 16)
        {
            return false;
        }

        var cpu = Set(CpuFanCurve, curve);
        var gpu = Set(GpuFanCurve, curve);
        return cpu == 1 && gpu == 1;
    }

    /// <summary>A device's status, or below 0 when this model does not have it.</summary>
    private int Get(uint device)
    {
        Span<byte> args = stackalloc byte[8];
        BitConverter.TryWriteBytes(args, device);
        return Call(Dsts, args) - 65536;
    }

    /// <returns>1 when the firmware accepted it.</returns>
    private int Set(uint device, int value)
    {
        Span<byte> args = stackalloc byte[8];
        BitConverter.TryWriteBytes(args, device);
        BitConverter.TryWriteBytes(args[4..], value);
        return Call(Devs, args);
    }

    /// <returns>1 when the firmware accepted it.</returns>
    private int Set(uint device, ReadOnlySpan<byte> values)
    {
        Span<byte> args = stackalloc byte[4 + values.Length];
        BitConverter.TryWriteBytes(args, device);
        values.CopyTo(args[4..]);
        return Call(Devs, args);
    }

    private int Call(uint method, ReadOnlySpan<byte> args)
    {
        var input = new byte[8 + args.Length];
        BitConverter.TryWriteBytes(input, method);
        BitConverter.TryWriteBytes(input.AsSpan(4), (uint)args.Length);
        args.CopyTo(input.AsSpan(8));

        var output = new byte[16];
        lock (_device)
        {
            if (!DeviceIoControl(_device, ControlCode, input, (uint)input.Length, output, (uint)output.Length, out _, 0))
            {
                return int.MinValue;
            }
        }

        return BitConverter.ToInt32(output, 0);
    }

    public void Dispose() => _device.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, nint overlapped);
}
