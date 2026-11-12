using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Reads and applies the Ally's 80% battery charge limit for the widget and the app, and puts it back
/// when the service starts or the handheld wakes from sleep. While the limit is on, the service is set to
/// start with Windows, so the limit is back on after a restart without anything having to ask for it.
/// </summary>
internal sealed class BatteryCareHost
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + AutoTdpProtocol.ServiceName;
    private const int AutomaticStart = 2;

    private readonly AsusAcpi? _acpi;
    private readonly object _gate = new();
    private readonly bool _supported;

    public BatteryCareHost(AsusAcpi? acpi)
    {
        _acpi = acpi;
        _supported = acpi?.CanSetChargeLimit == true;
        if (_supported && ReadSaved())
        {
            Apply(BatteryCare.CareLimit);

            // Undoing Replace Armoury Crate sets the service back to on demand and restarts it, so this is
            // where starting with Windows is put back.
            SetStartWithWindows(true);
        }
    }

    /// <summary>Whether the 80% limit is saved as on, so the service stays running to re-apply it after sleep.</summary>
    public bool IsSavedOn
    {
        get
        {
            lock (_gate)
            {
                return _supported && ReadSaved();
            }
        }
    }

    /// <summary>Re-applies the 80% limit after waking from sleep or hibernate, if it is turned on.</summary>
    public void RestoreIfEnabled()
    {
        lock (_gate)
        {
            if (_supported && ReadSaved())
            {
                Apply(BatteryCare.CareLimit);
            }
        }
    }

    /// <summary>The status with BatteryCare filled in, or null where this device cannot set it.</summary>
    public AutoTdpStatus AddTo(AutoTdpStatus status)
    {
        lock (_gate)
        {
            return status with { BatteryCare = _supported ? ReadSaved() : null };
        }
    }

    /// <summary>The 80% battery charge limit switched on or off, from the widget or the app.</summary>
    public AutoTdpStatus Answer(AutoTdpRequest request, AutoTdpStatus last) =>
        Set(request.BatteryCare) is { } problem ? last with { Error = problem } : last;

    /// <summary>Switches the 80% charge limit on or off, saving the choice and sending it to the firmware.</summary>
    /// <returns>Why it was not set, in plain words, or null.</returns>
    public string? Set(bool? on)
    {
        lock (_gate)
        {
            if (!_supported || _acpi is null)
            {
                return "Battery Care cannot be set on this device.";
            }

            if (on is not { } wanted)
            {
                return "Say whether Battery Care should be on or off.";
            }

            var limit = BatteryCare.LimitFor(wanted);
            if (!_acpi.SetChargeLimit(limit))
            {
                Log.Write($"The firmware refused battery charge limit {limit}%");
                return "The Ally's firmware would not take the battery charge limit.";
            }

            WriteSaved(wanted);
            Log.Write($"Battery charge limit set to {limit}%");
            SetStartWithWindows(BatteryCare.StartsWithWindows(wanted, AllyControlsStore.Replacing()));
            return null;
        }
    }

    private void Apply(int limit)
    {
        if (_acpi?.SetChargeLimit(limit) == true)
        {
            Log.Write($"Restored battery charge limit to {limit}%");
        }
    }

    /// <summary>Sets the service to start with Windows or only when asked, unless it already does.</summary>
    private static void SetStartWithWindows(bool automatic)
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(ServiceKey))
            {
                if (key?.GetValue("Start") is int start && (start == AutomaticStart) == automatic)
                {
                    return;
                }
            }

            var mode = automatic ? "auto" : "demand";
            using var sc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"),
                $"config {AutoTdpProtocol.ServiceName} start= {mode}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (sc is null || !sc.WaitForExit(5000) || sc.ExitCode != 0)
            {
                Log.Write($"Could not set TDP Engine to start {mode}");
                return;
            }

            Log.Write(automatic
                ? "TDP Engine now starts with Windows, to put the battery charge limit back after a restart"
                : "TDP Engine now starts only when asked");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException
            or Win32Exception or InvalidOperationException)
        {
            Log.Write($"Could not change how TDP Engine starts: {ex.Message}");
        }
    }

    private static bool ReadSaved()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BatteryCare.SettingsKey);
            return BatteryCare.IsEnabled(key?.GetValue(BatteryCare.LimitValue));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void WriteSaved(bool on)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(BatteryCare.SettingsKey);
            if (on)
            {
                key.SetValue(BatteryCare.LimitValue, BatteryCare.CareLimit, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(BatteryCare.LimitValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the battery charge limit: {ex.Message}");
        }
    }
}
