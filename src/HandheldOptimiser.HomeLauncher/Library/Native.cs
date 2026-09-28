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
