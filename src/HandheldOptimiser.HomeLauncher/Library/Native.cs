using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    public const uint ErrorSuccess = 0;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static partial uint XInputGetState(uint userIndex, out XInputState state);

    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PrivateExtractIcons(string file, int index, int cx, int cy,
        out nint icon, out uint iconId, uint count, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    public const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    /// <summary>
    /// Puts a window of the main app in front. Windows only allows this from the program the player is
    /// using, which the library is when a menu row is pressed.
    ///
    /// The main app runs as administrator and the library does not, so Windows will not let the library
    /// restore that window if it is minimised. The window is asked to restore itself instead, with a
    /// message the main app has agreed to take from the library (<paramref name="showMessage"/>); an
    /// older main app ignores it and is simply brought forward as it is.
    /// </summary>
    /// <returns>False when Windows would not give the window the foreground.</returns>
    public static bool BringToFront(nint window, int processId, string showMessage)
    {
        AllowSetForegroundWindow(processId);

        if (RegisterWindowMessage(showMessage) is var message and not 0)
        {
            PostMessage(window, message, 0, 0);
        }

        return SetForegroundWindow(window);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>
    /// The exe a process was started from, or null when it cannot be read. Asks only for the limited
    /// information any program may have about another, so it also works for one running as
    /// administrator, which Process.MainModule does not.
    /// </summary>
    public static unsafe string? ProcessPath(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            const int Capacity = 1024;
            var name = stackalloc char[Capacity];
            uint length = Capacity;
            return QueryFullProcessImageName(process, 0, name, ref length) ? new string(name, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
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
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool IsPwrHibernateAllowed();

    /// <summary>Sleeps or hibernates, and only returns once the device is awake again.</summary>
    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    // SYSTEM_POWER_CAPABILITIES, of which only the AoAc flag is read.
    private const int PowerCapabilitiesSize = 76;
    private const int AoAcOffset = 20;

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool GetPwrCapabilities(byte* capabilities);

    /// <summary>
    /// Whether the device sleeps with Modern Standby (always on, always connected), as most handhelds do.
    /// </summary>
    public static unsafe bool UsesModernStandby()
    {
        var capabilities = stackalloc byte[PowerCapabilitiesSize];
        return GetPwrCapabilities(capabilities) && capabilities[AoAcOffset] != 0;
    }

    private const uint WmSysCommand = 0x0112;
    private const nint ScMonitorPower = 0xF170;
    private const nint MonitorOff = 2;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Switches the display off, which is what sends a Modern Standby device to sleep.</summary>
    public static bool TurnDisplayOff(nint window) => PostMessage(window, WmSysCommand, ScMonitorPower, MonitorOff);

    public const uint WlanClientVersion = 2;
    public const int WlanInterfaceConnected = 1;
    public const int WlanOpcodeCurrentConnection = 7;

    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanCloseHandle(nint handle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanEnumInterfaces(nint handle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    public static partial uint WlanQueryInterface(nint handle, in Guid interfaceGuid, int opcode, nint reserved,
        out uint dataSize, out nint data, nint opcodeValueType);

    [LibraryImport("wlanapi.dll")]
    public static partial void WlanFreeMemory(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Window;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint Icon;
        public nint Process;
    }

    // Needed for verbs such as "properties" that come from the shell rather than the file type.
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const int SwShow = 5;

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteEx(ref ShellExecuteInfo info);

    /// <summary>Opens Explorer's Properties dialog for a file or folder.</summary>
    public static unsafe bool ShowProperties(string path)
    {
        fixed (char* verb = "properties", file = path)
        {
            var info = new ShellExecuteInfo
            {
                Size = (uint)sizeof(ShellExecuteInfo),
                Mask = SeeMaskInvokeIdList,
                Verb = verb,
                File = file,
                Show = SwShow
            };

            return ShellExecuteEx(ref info);
        }
    }

    /// <summary>
    /// The first icon in an exe or .ico at up to 256 px, which is what the store tiles show when a store
    /// keeps no cover art. Frozen, so it can be built off the UI thread.
    /// </summary>
    public static ImageSource? LoadIcon(string path, int size)
    {
        nint icon = 0;
        try
        {
            if (PrivateExtractIcons(path, 0, size, size, out icon, out _, 1, 0) == 0 || icon == 0)
            {
                return null;
            }

            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (icon != 0)
            {
                DestroyIcon(icon);
            }
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicy);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    /// <summary>The active power plan's id, or null when it cannot be read.</summary>
    public static Guid? ActivePowerPlan()
    {
        if (PowerGetActiveScheme(0, out var policy) != ErrorSuccess || policy == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(policy);
        }
        finally
        {
            LocalFree(policy);
        }
    }

    // Not in the Windows SDK headers, but exported by powrprof.dll since Windows 10 1709. The Settings
    // app's power mode control calls these.
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveOverlayScheme(Guid overlay);

    /// <summary>DEVMODEW, laid out for a display rather than a printer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    public const uint DmPelsWidth = 0x00080000;
    public const uint DmPelsHeight = 0x00100000;
    public const uint DmDisplayFrequency = 0x00400000;
    public const uint DmInterlaced = 0x00000002;
    public const int DispChangeSuccessful = 0;
    public const int DispChangeRestart = 1;

    private const int EnumCurrentSettings = -1;
    private const uint CdsUpdateRegistry = 0x00000001;

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsEx(string? deviceName, ref DevMode mode, nint window, uint flags, nint parameters);

    /// <summary>The main screen's current resolution and refresh rate.</summary>
    public static bool CurrentDisplayMode(out DevMode mode) => DisplayMode(EnumCurrentSettings, out mode);

    /// <summary>One of the modes the main screen offers, numbered from 0; false past the last one.</summary>
    public static unsafe bool DisplayMode(int index, out DevMode mode)
    {
        mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        return EnumDisplaySettings(null, index, ref mode);
    }

    /// <summary>Switches the main screen to a mode, for this user, kept after a restart.</summary>
    /// <returns>A DISP_CHANGE code; 0 is success.</returns>
    public static unsafe int ChangeDisplayMode(ref DevMode mode)
    {
        mode.Size = (ushort)sizeof(DevMode);
        return ChangeDisplaySettingsEx(null, ref mode, 0, CdsUpdateRegistry, 0);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    private const int SwRestore = 9;

    /// <summary>
    /// Puts another program's window in front, restored if it was minimised. Windows only allows this
    /// while the library is the program in front.
    /// </summary>
    public static bool SwitchTo(nint window)
    {
        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        return SetForegroundWindow(window);
    }

    // ----- Quick Resume -----

    private const uint ProcessSuspendResume = 0x0800;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// A handle that can pause and resume the process, or 0 when Windows refuses one: for a process
    /// running as administrator, or one an anti-cheat driver protects. Close it with <see cref="CloseProcess"/>.
    /// </summary>
    public static nint OpenForPause(int processId) =>
        OpenProcess(ProcessSuspendResume | ProcessQueryLimitedInformation, false, processId);

    public static void CloseProcess(nint process) => CloseHandle(process);

    /// <summary>Stops every thread of the process. Each call needs one <see cref="ResumeProcess"/>.</summary>
    public static bool SuspendProcess(nint process) => NtSuspendProcess(process) >= 0;

    public static bool ResumeProcess(nint process) => NtResumeProcess(process) >= 0;

    /// <summary>When the process started, as a FILETIME, which tells it apart from a later one given the same id.</summary>
    public static long ProcessStarted(nint process) => GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : 0;

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    [LibraryImport("user32.dll")]
    private static unsafe partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(nint window, char* name, int capacity);

    /// <summary>Raised on the thread that called <see cref="WatchForeground"/> whenever another window comes to the front.</summary>
    public static event Action<nint>? ForegroundChanged;

    /// <summary>Starts raising <see cref="ForegroundChanged"/>. Needs a message loop on this thread, as the UI thread has.</summary>
    public static unsafe nint WatchForeground() =>
        SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &OnWinEvent, 0, 0, WinEventOutOfContext);

    public static void StopWatchingForeground(nint hook) => UnhookWinEvent(hook);

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint winEvent, nint window, int objectId, int childId, uint thread, uint time)
    {
        // An exception cannot cross back into Windows, so it ends here.
        try
        {
            ForegroundChanged?.Invoke(window);
        }
        catch (Exception ex)
        {
            Program.Log($"Foreground change failed: {ex.Message}");
        }
    }

    /// <summary>The id of the process the window belongs to, or 0.</summary>
    public static int WindowProcess(nint window) => GetWindowThreadProcessId(window, out var processId) != 0 ? processId : 0;

    /// <summary>
    /// Whether the window is the stand-in Windows puts up for a program that has stopped responding,
    /// which is what a paused game's window turns into if it is brought to the front.
    /// </summary>
    public static unsafe bool IsGhostWindow(nint window)
    {
        const int Capacity = 16;
        var name = stackalloc char[Capacity];
        var length = GetClassName(window, name, Capacity);
        return length > 0 && new string(name, 0, length) == "Ghost";
    }

    // The display turning off and on, which is how a Modern Standby device's sleep and wake show up.
    public static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    public const int WmPowerBroadcast = 0x0218;
    public const int PbtApmSuspend = 0x0004;
    public const int PbtApmResumeSuspend = 0x0007;
    public const int PbtApmResumeAutomatic = 0x0012;
    public const int PbtPowerSettingChange = 0x8013;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(nint notification);

    /// <summary>POWERBROADCAST_SETTING, for a setting whose data is one DWORD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint QdcOnlyActivePaths = 2;
    private const int DisplayConfigGetSourceName = 1;

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

    /// <summary>DISPLAYCONFIG_MODE_INFO, which is only passed through.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayModeInfo
    {
        public uint InfoType;
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

    /// <summary>MONITORINFOEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MonitorInfo
    {
        public uint Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static unsafe partial int QueryDisplayConfig(uint flags, ref uint pathCount, DisplayPath* paths,
        ref uint modeCount, DisplayModeInfo* modes, nint topologyId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int DisplayConfigGetDeviceInfo(SourceDeviceName* request);

    /// <summary>The \\.\DISPLAYn name of the screen a window is on, or null when it cannot be read.</summary>
    public static unsafe string? ScreenOf(nint window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        return monitor != 0 && GetMonitorInfo(monitor, ref info) ? new string(info.Device) : null;
    }

    /// <summary>
    /// Every screen Windows draws, by its \\.\DISPLAYn name, with the kind of connection each place it
    /// shows on uses (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY). A screen duplicated on two displays is listed
    /// twice. Empty when Windows will not say.
    /// </summary>
    public static unsafe List<(string Screen, uint Technology)> ScreenConnections()
    {
        var found = new List<(string, uint)>();

        // Displays can come and go between the two calls, which then asks for a bigger buffer.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return found;
            }

            var paths = new DisplayPath[pathCount];
            var modes = new DisplayModeInfo[modeCount];
            int result;
            fixed (DisplayPath* pathBuffer = paths)
            fixed (DisplayModeInfo* modeBuffer = modes)
            {
                result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, 0);
            }

            // ERROR_INSUFFICIENT_BUFFER: try again with the new sizes.
            if (result == 122)
            {
                continue;
            }

            if (result != 0)
            {
                return found;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var request = new SourceDeviceName
                {
                    Type = DisplayConfigGetSourceName,
                    Size = (uint)sizeof(SourceDeviceName),
                    Adapter = paths[i].SourceAdapter,
                    Id = paths[i].SourceId
                };

                if (DisplayConfigGetDeviceInfo(&request) == 0)
                {
                    found.Add((new string(request.GdiDeviceName), paths[i].OutputTechnology));
                }
            }

            return found;
        }

        return found;
    }

    /// <summary>
    /// Hands the process's pages back to Windows. The library sits behind the game for hours, so its
    /// working set should be what the game can use, not what the library last touched.
    /// </summary>
    public static void TrimWorkingSet()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }
}
