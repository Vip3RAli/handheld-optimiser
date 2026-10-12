using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Windows' own on-screen keyboard, which a controller can type on. Windows brings it up by itself for a
/// tap on a text box, but not for a box focused from the controller, nor while a keyboard is plugged in,
/// so the search box asks for it.
/// </summary>
internal static class TouchKeyboard
{
    // The program behind the keyboard, which has to be running before it can be asked for.
    private static readonly string TabTip = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        "microsoft shared", "ink", "TabTip.exe");

    /// <summary>Brings the keyboard up, if it is not up already.</summary>
    public static void Show(nint window) => Set(window, show: true);

    /// <summary>Puts the keyboard away, if it is up.</summary>
    public static void Hide(nint window) => Set(window, show: false);

    private static void Set(nint window, bool show)
    {
        try
        {
            // Windows only offers to switch it, so it is only asked when it is not already as wanted.
            if (IsShowing() == show)
            {
                return;
            }

            try
            {
                Toggle(window);
            }
            catch (COMException) when (show && File.Exists(TabTip))
            {
                // Not started yet in this session. On some builds starting it also brings the keyboard up.
                using var started = Process.Start(new ProcessStartInfo(TabTip) { UseShellExecute = true });
                Thread.Sleep(300);
                if (!IsShowing())
                {
                    Toggle(window);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or IOException)
        {
            Program.Log($"The on-screen keyboard could not be {(show ? "shown" : "hidden")}: {ex.Message}");
        }
    }

    private static void Toggle(nint window)
    {
        var keyboard = (ITipInvocation)new TipInvocation();
        try
        {
            keyboard.Toggle(window);
        }
        finally
        {
            Marshal.ReleaseComObject(keyboard);
        }
    }

    /// <summary>Whether the keyboard is on screen now, as far as Windows says. False when it cannot be asked.</summary>
    public static bool IsShowing()
    {
        try
        {
            return IsOnScreen();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    // It covers a part of the screen while it is up.
    private static bool IsOnScreen()
    {
        var pane = (IFrameworkInputPane)new FrameworkInputPane();
        try
        {
            return pane.Location(out var area) == 0 && area.Right > area.Left && area.Bottom > area.Top;
        }
        finally
        {
            Marshal.ReleaseComObject(pane);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376")]
    private class TipInvocation;

    [ComImport]
    [Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(nint window);
    }

    [ComImport]
    [Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
    private class FrameworkInputPane;

    [ComImport]
    [Guid("5752238B-24F0-495A-82F1-2FD593056796")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameworkInputPane
    {
        [PreserveSig]
        int Advise(nint window, nint handler, out int cookie);

        [PreserveSig]
        int AdviseWithHWND(nint window, nint handler, out int cookie);

        [PreserveSig]
        int Unadvise(int cookie);

        [PreserveSig]
        int Location(out Rect area);
    }
}
