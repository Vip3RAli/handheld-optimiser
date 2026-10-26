using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The Ally's GPU memory, read and set for the widget and the app. A new size is used from the next restart,
/// so the size Windows has now is the firmware's code when Windows started. The service ends itself when
/// idle, so that code is saved in HKLM with the time Windows started rather than held, on the first GPU
/// memory request of each start. Only this sets the size, so the first read always comes before any set.
/// </summary>
internal sealed class GpuMemoryHost(AsusAcpi? acpi)
{
    private const string Key = @"SOFTWARE\HandheldOptimiser\GpuMemory";
    private const string BootCodeValue = "BootCode";
    private const string StartedValue = "WindowsStarted";

    private readonly object _gate = new();
    private int? _bootCode;

    /// <summary>The status with the GPU memory filled in: empty sizes on offer when this device cannot set it.</summary>
    public AutoTdpStatus AddTo(AutoTdpStatus status)
    {
        lock (_gate)
        {
            if (acpi?.ReadGpuMemory() is not { } code || BootCode(code) is not { } boot)
            {
                return status with { GpuMemoryNow = null, GpuMemoryNext = null, GpuMemoryOffered = [] };
            }

            var (now, next) = GpuMemory.Describe(boot, code);
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
            if (acpi?.ReadGpuMemory() is not { } code)
            {
                return "GPU memory cannot be set on this device.";
            }

            if (size is not { } wanted || GpuMemory.CodeOf(wanted) is not { } newCode)
            {
                return "That GPU memory size is not one on offer.";
            }

            // Read, and saved for this start, before anything is changed.
            BootCode(code);
            if (!acpi.SetGpuMemory(newCode))
            {
                Log.Write($"The firmware refused GPU memory {Name(wanted)} (code 0x{newCode:X})");
                return "The Ally's firmware would not take that GPU memory size.";
            }

            Log.Write($"GPU memory set to {Name(wanted)}, used from the next restart");
            return null;
        }
    }

    private static string Name(int size) => size == 0 ? "Auto" : $"{size} GB";

    /// <summary>The code Windows started with: the saved one from this start, or the firmware's now, saved as this start's.</summary>
    private int? BootCode(int firmwareCode)
    {
        if (_bootCode is { } known)
        {
            return known;
        }

        var started = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(Key);
            _bootCode = GpuMemory.BootCode(key.GetValue(BootCodeValue) as int?, key.GetValue(StartedValue) as long?, started);
            if (_bootCode is null)
            {
                _bootCode = firmwareCode;
                key.SetValue(BootCodeValue, firmwareCode, RegistryValueKind.DWord);
                key.SetValue(StartedValue, started.Ticks, RegistryValueKind.QWord);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read or save the GPU memory Windows started with: {ex.Message}");
            _bootCode = firmwareCode;
        }

        return _bootCode;
    }
}
