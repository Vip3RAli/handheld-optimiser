using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What the Ally's own panel is doing while a monitor is in use.</summary>
internal enum PanelState
{
    /// <summary>Turned off, as with "Second screen only".</summary>
    Off,

    /// <summary>Showing the same picture as a monitor, as with "Duplicate".</summary>
    Shared,

    /// <summary>A screen of its own beside the monitor, as with "Extend".</summary>
    Own
}

/// <summary>The screens as Windows has them now.</summary>
/// <param name="MonitorOn">Whether a monitor or TV is in use, which is what docked means here.</param>
/// <param name="Panel">What the Ally's panel is doing.</param>
/// <param name="PanelBounds">Where the panel is on the desktop, in pixels, while it is a screen of its own.</param>
internal readonly record struct ScreenSetup(bool MonitorOn, PanelState Panel, ScreenRect? PanelBounds);

/// <summary>
/// Turns the Ally's panel on beside the monitor for the stats screen, at 720p and under the monitor, and puts
/// the screens back afterwards. Nothing is saved to Windows' own display settings: the change lasts until it
/// is put back, the Ally is undocked, or Windows restarts, which all bring back the player's own setup.
/// </summary>
internal static partial class AllyScreen
{
    private const uint QdcOnlyActivePaths = 2;
    private const uint ModeIndexInvalid = 0xFFFFFFFF;
    private const uint SourceModeType = 1;
    private const int ErrorInsufficientBuffer = 122;

    private const uint SdcTopologyClone = 0x00000002;
    private const uint SdcTopologyExtend = 0x00000004;
    private const uint SdcTopologyExternal = 0x00000008;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcApply = 0x00000080;
    private const uint SdcAllowChanges = 0x00000400;

    private const int DisplayConfigGetSourceName = 1;

    /// <summary>The screens as they are now. When Windows will not say, nothing is in use.</summary>
    public static ScreenSetup Read()
    {
        if (Query() is not { } config)
        {
            return new ScreenSetup(false, PanelState.Off, null);
        }

        var (paths, modes) = config;
        var monitorOn = paths.Any(p => !Displays.IsBuiltIn(p.OutputTechnology));
        var panel = Array.FindIndex(paths, p => Displays.IsBuiltIn(p.OutputTechnology));
        if (panel < 0)
        {
            return new ScreenSetup(monitorOn, PanelState.Off, null);
        }

        var shared = paths.Where((p, i) => i != panel && SameSource(p, paths[panel])).Any();
        if (shared)
        {
            return new ScreenSetup(monitorOn, PanelState.Shared, null);
        }

        return new ScreenSetup(monitorOn, PanelState.Own, SourceRect(modes, paths[panel].SourceModeIndex));
    }

    /// <summary>Where each monitor is on the desktop, in pixels, leaving out the Ally's panel.</summary>
    public static IReadOnlyList<ScreenRect> MonitorBounds()
    {
        if (Query() is not { } config)
        {
            return [];
        }

        var (paths, modes) = config;
        return paths.Where(p => !Displays.IsBuiltIn(p.OutputTechnology))
            .Select(p => SourceRect(modes, p.SourceModeIndex))
            .OfType<ScreenRect>()
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Turns the panel on as a screen of its own beside the monitor, then makes it 720p and puts it under the
    /// monitor, which stays the main screen.
    /// </summary>
    /// <returns>Whether the screens were changed, so they need putting back.</returns>
    public static unsafe bool ShowBesideMonitor()
    {
        var result = SetDisplayConfig(0, null, 0, null, SdcApply | SdcTopologyExtend);
        if (result != 0)
        {
            Program.Log($"Stats screen: Windows would not turn the Ally's screen on beside the monitor (error {result})");
            return false;
        }

        Program.Log("Stats screen: turned the Ally's screen on beside the monitor");
        Arrange();
        return true;
    }

    /// <summary>Puts the screens back as they were before <see cref="ShowBesideMonitor"/>.</summary>
    public static unsafe void PutBack(PanelState before)
    {
        var topology = before == PanelState.Shared ? SdcTopologyClone : SdcTopologyExternal;
        var result = SetDisplayConfig(0, null, 0, null, SdcApply | topology);
        Program.Log(result == 0
            ? $"Stats screen: screens put back to {(before == PanelState.Shared ? "duplicate" : "second screen only")}"
            : $"Stats screen: Windows would not put the screens back (error {result})");
    }

    /// <summary>The panel at 720p, under the main monitor, which is kept at the desktop's corner.</summary>
    private static void Arrange()
    {
        if (Query() is not { } config)
        {
            return;
        }

        var (paths, modes) = config;
        var panel = Array.FindIndex(paths, p => Displays.IsBuiltIn(p.OutputTechnology));
        if (panel < 0 || paths[panel].SourceModeIndex is var panelMode && !IsSourceMode(modes, panelMode))
        {
            Program.Log("Stats screen: the Ally's screen is not in Windows' new setup");
            return;
        }

        // Each monitor's picture once, though a monitor duplicated on two displays has two paths.
        var monitorModes = paths.Where(p => !Displays.IsBuiltIn(p.OutputTechnology))
            .Select(p => p.SourceModeIndex)
            .Where(i => IsSourceMode(modes, i) && i != panelMode)
            .Distinct()
            .ToList();

        var current = SourceRect(modes, panelMode)!.Value;
        var (width, height) = ScreenStatsLayout.PanelSize(OfferedSizes(paths[panel]), current.Width, current.Height);
        var (monitors, place) = ScreenStatsLayout.Arrange(monitorModes.Select(i => SourceRect(modes, i)!.Value).ToList(), width, height);

        for (var i = 0; i < monitorModes.Count; i++)
        {
            SetSourceRect(modes, monitorModes[i], monitors[i]);
        }

        SetSourceRect(modes, panelMode, place);
        var result = Apply(paths, modes);

        // A size Windows will not take is dropped, and the panel kept as it is, just moved under the monitor.
        if (result != 0 && (width, height) != (current.Width, current.Height))
        {
            Program.Log($"Stats screen: Windows would not set the Ally's screen to {width}x{height} (error {result}), so it stays at {current.Width}x{current.Height}");
            SetSourceRect(modes, panelMode, place with { Width = current.Width, Height = current.Height, X = (monitors[0].Width - current.Width) / 2 });
            result = Apply(paths, modes);
        }

        Program.Log(result == 0
            ? $"Stats screen: the Ally's screen is {SourceRect(modes, panelMode)!.Value.Width}x{SourceRect(modes, panelMode)!.Value.Height}, under the monitor"
            : $"Stats screen: Windows would not move the Ally's screen under the monitor (error {result}); it stays where Windows put it");
    }

    private static unsafe int Apply(DisplayPath[] paths, DisplayModeInfo[] modes)
    {
        fixed (DisplayPath* pathBuffer = paths)
        fixed (DisplayModeInfo* modeBuffer = modes)
        {
            return SetDisplayConfig((uint)paths.Length, pathBuffer, (uint)modes.Length, modeBuffer,
                SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges);
        }
    }

    /// <summary>The sizes the panel lists, from its \\.\DISPLAYn name.</summary>
    private static unsafe List<(int Width, int Height)> OfferedSizes(DisplayPath panel)
    {
        var sizes = new List<(int, int)>();
        var request = new SourceDeviceName
        {
            Type = DisplayConfigGetSourceName,
            Size = (uint)sizeof(SourceDeviceName),
            Adapter = panel.SourceAdapter,
            Id = panel.SourceId
        };

        if (DisplayConfigGetDeviceInfo(&request) != 0)
        {
            return sizes;
        }

        var device = new string(request.GdiDeviceName);
        var mode = new Native.DevMode { Size = (ushort)sizeof(Native.DevMode) };
        for (var i = 0; EnumDisplaySettings(device, i, ref mode); i++)
        {
            sizes.Add(((int)mode.PelsWidth, (int)mode.PelsHeight));
        }

        return sizes;
    }

    private static bool SameSource(DisplayPath a, DisplayPath b) =>
        a.SourceId == b.SourceId && a.SourceAdapter.LowPart == b.SourceAdapter.LowPart && a.SourceAdapter.HighPart == b.SourceAdapter.HighPart;

    private static bool IsSourceMode(DisplayModeInfo[] modes, uint index) =>
        index != ModeIndexInvalid && index < modes.Length && modes[index].InfoType == SourceModeType;

    private static ScreenRect? SourceRect(DisplayModeInfo[] modes, uint index) => IsSourceMode(modes, index)
        ? new ScreenRect(modes[index].X, modes[index].Y, (int)modes[index].Width, (int)modes[index].Height)
        : null;

    private static void SetSourceRect(DisplayModeInfo[] modes, uint index, ScreenRect rect)
    {
        modes[index].X = rect.X;
        modes[index].Y = rect.Y;
        modes[index].Width = (uint)rect.Width;
        modes[index].Height = (uint)rect.Height;
    }

    /// <summary>The screens Windows draws now and their modes, or null when it will not say.</summary>
    private static unsafe (DisplayPath[] Paths, DisplayModeInfo[] Modes)? Query()
    {
        // Displays can come and go between the two calls, which then asks for a bigger buffer.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return null;
            }

            var paths = new DisplayPath[pathCount];
            var modes = new DisplayModeInfo[modeCount];
            int result;
            fixed (DisplayPath* pathBuffer = paths)
            fixed (DisplayModeInfo* modeBuffer = modes)
            {
                result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, 0);
            }

            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            return result == 0 ? (paths[..(int)pathCount], modes[..(int)modeCount]) : null;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO: a screen Windows draws (the source) and where it shows (the target).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        public Luid SourceAdapter;
        public uint SourceId;
        public uint SourceModeIndex;
        public uint SourceStatus;
        public Luid TargetAdapter;
        public uint TargetId;
        public uint TargetModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO. Only a source mode's fields are named: its size and where it is on the desktop
    /// (DISPLAYCONFIG_SOURCE_MODE). A target mode is passed through as it is.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME: the \\.\DISPLAYn name of a source.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SourceDeviceName
    {
        public int Type;
        public uint Size;
        public Luid Adapter;
        public uint Id;
        public fixed char GdiDeviceName[32];
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static unsafe partial int QueryDisplayConfig(uint flags, ref uint pathCount, DisplayPath* paths,
        ref uint modeCount, DisplayModeInfo* modes, nint topologyId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int SetDisplayConfig(uint pathCount, DisplayPath* paths, uint modeCount,
        DisplayModeInfo* modes, uint flags);

    [LibraryImport("user32.dll")]
    private static unsafe partial int DisplayConfigGetDeviceInfo(SourceDeviceName* request);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string deviceName, int modeNumber, ref Native.DevMode mode);
}

/// <summary>
/// Keeps the Ally's panel on as the stats screen while docked, and remembers what to put back. It is tried
/// once each time the Ally is docked: should the player turn the panel off again, as with Win+P, it is left off.
/// </summary>
internal sealed class PanelKeeper
{
    // What the panel was doing before it was turned on here, or null when nothing was changed.
    private PanelState? _changedFrom;
    private bool _tried;

    // Whether the panel was turned off here while the screen is locked, to be turned on again when unlocked.
    private bool _offWhileLocked;

    /// <summary>Where the stats go, in desktop pixels, or null when there is nowhere for them now.</summary>
    public ScreenRect? Keep()
    {
        var now = AllyScreen.Read();

        // Undocked: Windows has already given the panel back to the player, so there is nothing to put back.
        if (!now.MonitorOn)
        {
            _tried = false;
            _changedFrom = null;
            _offWhileLocked = false;
            return null;
        }

        // Unlocked again: the panel is turned back on, once, as when the Ally is docked.
        if (_offWhileLocked)
        {
            _offWhileLocked = false;
            _tried = false;
        }

        if (now.Panel == PanelState.Own)
        {
            return now.PanelBounds;
        }

        if (_tried)
        {
            return null;
        }

        _tried = true;

        // Turned on again after the lock screen, it still goes back to what the player had at first.
        if (AllyScreen.ShowBesideMonitor())
        {
            _changedFrom ??= now.Panel;
        }

        return AllyScreen.Read() is { MonitorOn: true, Panel: PanelState.Own } after ? after.PanelBounds : null;
    }

    /// <summary>
    /// Turns the panel off while the screen is locked, rather than leave it lit beside the lock screen. Only
    /// when it was turned on here and is on now: a panel the player turned off, as with Win+P, stays off.
    /// </summary>
    public void TurnOffWhileLocked()
    {
        if (_changedFrom is null || _offWhileLocked || AllyScreen.Read() is not { MonitorOn: true, Panel: PanelState.Own })
        {
            return;
        }

        _offWhileLocked = true;
        Program.Log("Stats screen: the screen is locked, so the Ally's screen is turned off until it is unlocked");
        AllyScreen.PutBack(PanelState.Off);
    }

    /// <summary>Puts the screens back when they were changed here.</summary>
    public void PutBack()
    {
        _offWhileLocked = false;
        if (_changedFrom is { } before)
        {
            _changedFrom = null;
            AllyScreen.PutBack(before);
        }
    }
}
