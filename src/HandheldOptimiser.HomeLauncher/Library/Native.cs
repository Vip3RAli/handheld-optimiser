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
