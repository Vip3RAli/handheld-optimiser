using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The back paddle's screenshot: the screen the game is on, saved as a PNG in Pictures\Screenshots, where
/// Windows' own Win+PrtScn puts them. It is taken here, not through Game Bar, because Game Bar's captures
/// are switched off by the app's own "Turn off background game recording" tweak (AllowGameDVR 0 stops its
/// screenshots too), and that is how it was on this Ally when the paddle was first tried on 2026-11-11.
/// The picture comes from <see cref="ScreenCapture"/>, which also sees a game that owns the screen, and
/// from a GDI copy of the desktop only when that is refused.
/// </summary>
internal static partial class Screenshot
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayered = 0x40000000;
    private const uint MonitorNearest = 2;
    private const uint BeepOk = 0;

    // FOLDERID_Screenshots, which the player can move. Pictures\Screenshots when it cannot be read.
    private static readonly Guid ScreenshotsFolder = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary>Takes the screenshot and beeps once it is saved.</summary>
    /// <returns>The file saved, or null when it could not be taken.</returns>
    public static string? Take()
    {
        var window = GetForegroundWindow();
        var monitor = MonitorFromWindow(window, MonitorNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info) || info.Monitor.Right <= info.Monitor.Left || info.Monitor.Bottom <= info.Monitor.Top)
        {
            Program.Log("Screenshot: the screen could not be found");
            return null;
        }

        var bounds = info.Monitor;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;

        try
        {
            // Duplication first: it is the only one of the two that sees a game that owns the screen.
            BitmapSource? image = null;
            if (ScreenCapture.Capture(monitor, out var why) is { } picture)
            {
                image = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgr32, null,
                    picture.Pixels, picture.Stride);
            }
            else
            {
                Program.Log($"Screenshot: {why}, so the desktop is copied instead");
                image = CopyDesktop(bounds, width, height);
            }

            if (image is null)
            {
                return null;
            }

            var folder = Folder();
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName(WindowTitle(window), DateTime.Now));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            MessageBeep(BeepOk);
            Program.Log($"Screenshot saved: {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ExternalException)
        {
            Program.Log($"Screenshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The GDI copy of the desktop, kept for when the screen cannot be duplicated (a rotated screen, a
    /// remote session). It comes out black over a game that owns the screen.
    /// </summary>
    private static BitmapSource? CopyDesktop(Rect bounds, int width, int height)
    {
        nint screen = 0, memory = 0, bitmap = 0, old = 0;
        try
        {
            screen = GetDC(0);
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (screen == 0 || memory == 0 || bitmap == 0)
            {
                Program.Log("Screenshot: Windows would not give a bitmap to copy into");
                return null;
            }

            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, SourceCopy | CaptureLayered))
            {
                Program.Log($"Screenshot: the copy failed (error {Marshal.GetLastPInvokeError()})");
                return null;
            }

            SelectObject(memory, old);
            old = 0;

            // Frozen so the picture owns its pixels before the bitmap behind it is deleted.
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (old != 0)
            {
                SelectObject(memory, old);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                DeleteDC(memory);
            }

            if (screen != 0)
            {
                ReleaseDC(0, screen);
            }
        }
    }

    /// <summary>The file's name: the game's window title, or "Screenshot", then the time, as "Title 2026-11-11 19-30-50.png".</summary>
    public static string FileName(string? title, DateTime when)
    {
        var name = string.Concat((title ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (name.Length == 0)
        {
            name = "Screenshot";
        }
        else if (name.Length > 60)
        {
            name = name[..60].Trim();
        }

        return $"{name} {when:yyyy-MM-dd HH-mm-ss}.png";
    }

    private static string Folder()
    {
        if (SHGetKnownFolderPath(ScreenshotsFolder, 0, 0, out var path) == 0 && path != 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(path) is { Length: > 0 } known)
                {
                    return known;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    private static string? WindowTitle(nint window)
    {
        var buffer = new char[256];
        var length = GetWindowTextW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint window, [Out] char[] text, int length);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
}
