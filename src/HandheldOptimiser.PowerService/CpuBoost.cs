using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Turns the processor's boost on or off, through Windows' "Processor performance boost mode" on the
/// active power plan. Off is Disabled for both plugged in and on battery. On puts back the modes the plan
/// had before boost was turned off here, and Aggressive, Windows' usual, for any that was already
/// Disabled: some makers ship boost off on battery, and On should mean on.
///
/// Windows keeps the setting in the plan, so it holds without the service. The modes from before are
/// saved in HKLM so On can put them back after a restart.
/// </summary>
internal static partial class CpuBoost
{
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid BoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");

    private const string SavedKey = @"SOFTWARE\HandheldOptimiser\CpuBoost";
    private const string SavedPluggedIn = "PluggedInBefore";
    private const string SavedOnBattery = "OnBatteryBefore";

    /// <summary>Whether boost is on now for the power source in use, or null when Windows will not say.</summary>
    public static bool? Read() => ReadModes() is { } modes ? CpuBoostRules.IsOn(modes, OnBattery()) : null;

    /// <summary>Turns boost on or off. Returns why it could not, or null.</summary>
    public static string? Set(bool on)
    {
        if (ReadModes() is not { } current)
        {
            return "Windows would not say what the power plan's boost setting is.";
        }

        var saved = LoadSaved();
        if (!on)
        {
            Save(CpuBoostRules.ToSave(current, saved));
        }

        var target = CpuBoostRules.Target(on, saved);
        if (!WriteModes(target))
        {
            return "Windows would not change the power plan's boost setting.";
        }

        Log.Write($"CPU boost {(on ? "on" : "off")}: plugged in {target.PluggedIn}, on battery {target.OnBattery}");
        return null;
    }

    private static BoostModes? ReadModes()
    {
        if (ActiveScheme() is not { } scheme)
        {
            return null;
        }

        var subgroup = ProcessorSubgroup;
        var setting = BoostModeSetting;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var pluggedIn) != 0 ||
            PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var onBattery) != 0)
        {
            return null;
        }

        return new BoostModes(pluggedIn, onBattery);
    }

    private static bool WriteModes(BoostModes modes)
    {
        if (ActiveScheme() is not { } scheme)
        {
            return false;
        }

        var subgroup = ProcessorSubgroup;
        var setting = BoostModeSetting;

        // A change to the active plan takes effect once the plan is set active again.
        return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, modes.PluggedIn) == 0 &&
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, modes.OnBattery) == 0 &&
            PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
    }

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static bool OnBattery() => GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;

    /// <summary>The modes saved from before boost was turned off, or Disabled for each when none are.</summary>
    private static BoostModes LoadSaved()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SavedKey);
            return new BoostModes(
                key?.GetValue(SavedPluggedIn) is int pluggedIn ? (uint)pluggedIn : CpuBoostRules.Disabled,
                key?.GetValue(SavedOnBattery) is int onBattery ? (uint)onBattery : CpuBoostRules.Disabled);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not read the saved boost modes: {ex.Message}");
            return new BoostModes(CpuBoostRules.Disabled, CpuBoostRules.Disabled);
        }
    }

    private static void Save(BoostModes modes)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SavedKey);
            key.SetValue(SavedPluggedIn, (int)modes.PluggedIn, RegistryValueKind.DWord);
            key.SetValue(SavedOnBattery, (int)modes.OnBattery, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not save the boost modes: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicy);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid scheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
}
