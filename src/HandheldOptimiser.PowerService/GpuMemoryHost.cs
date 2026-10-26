using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Ally's GPU memory, read and set for the widget and the app. The firmware holds the size used from the
/// next restart. The size Windows has now is what the graphics driver reported as it started, the same
/// figure the app's Dashboard shows, so a restart of any kind, Fast Startup included, is seen as it happens.
/// </summary>
internal sealed class GpuMemoryHost(AsusAcpi? acpi)
{
    // Every display adapter Windows has a driver for, one numbered key each.
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string MemorySizeValue = "HardwareInformation.qwMemorySize";

    private readonly object _gate = new();

    // What the driver reported, read once it has reported something: it does not change until Windows restarts.
    private int? _now;

    /// <summary>The status with the GPU memory filled in: empty sizes on offer when this device cannot set it.</summary>
    public AutoTdpStatus AddTo(AutoTdpStatus status)
    {
        lock (_gate)
        {
            if (acpi?.ReadGpuMemory() is not { } code)
            {
                return status with { GpuMemoryNow = null, GpuMemoryNext = null, GpuMemoryOffered = [] };
            }

            var (now, next) = GpuMemory.Describe(Now(), code);
            return status with { GpuMemoryNow = now, GpuMemoryNext = next, GpuMemoryOffered = GpuMemory.Sizes };
        }
    }

    /// <summary>Sets the size, used from the next restart. Setting the size Windows has now clears a change that waits.</summary>
    /// <param name="size">In GB, 0 for Auto.</param>
    /// <returns>Why it was not set, in plain words, or null.</returns>
    public string? Set(int? size)
    {
        lock (_gate)
        {
            if (acpi?.ReadGpuMemory() is null)
            {
                return "GPU memory cannot be set on this device.";
            }

            if (size is not { } wanted || GpuMemory.CodeOf(wanted) is not { } code)
            {
                return "That GPU memory size is not one on offer.";
            }

            if (!acpi.SetGpuMemory(code))
            {
                Log.Write($"The firmware refused GPU memory {Name(wanted)} (code 0x{code:X})");
                return "The Ally's firmware would not take that GPU memory size.";
            }

            Log.Write($"GPU memory set to {Name(wanted)}, used from the next restart");
            return null;
        }
    }

    private static string Name(int size) => size == 0 ? "Auto" : $"{size} GB";

    /// <summary>
    /// The size Windows has now. With more than one adapter, as with an external GPU, the built-in one is the
    /// one with the least memory of its own, as the Dashboard takes it.
    /// </summary>
    private int? Now()
    {
        if (_now is { } known)
        {
            return known;
        }

        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            var sizes = adapters?.GetSubKeyNames()
                .Select(name => ReadBytes(adapters, name))
                .OfType<long>()
                .Where(bytes => bytes > 0)
                .ToList() ?? [];

            _now = sizes.Count > 0 ? GpuMemory.SizeFromBytes(sizes.Min()) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the GPU memory Windows has: {ex.Message}");
        }

        return _now;
    }

    private static long? ReadBytes(RegistryKey adapters, string name)
    {
        // Numbered keys only: the class key also holds "Properties", which no one may open.
        if (!int.TryParse(name, out _))
        {
            return null;
        }

        using var adapter = adapters.OpenSubKey(name);
        return adapter?.GetValue(MemorySizeValue) switch
        {
            long bytes => bytes,
            byte[] { Length: 8 } raw => BitConverter.ToInt64(raw),
            _ => null
        };
    }
}
