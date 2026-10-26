using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Keeps the Ally's panel for the stats screen alone while it is a screen beside the monitor. Windows treats
/// it as any second screen, so it puts other windows on it: every open window went there when the panel was
/// turned on. A window found on the panel is moved to the main monitor, at once through Windows' window
/// events and once a second as well, and the mouse is kept off the panel. Windows cannot keep the panel for
/// one program alone: "Remove display from desktop" is not offered for the Ally's own screen.
/// </summary>
internal sealed unsafe partial class PanelGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjectWindow = 0;

    private const uint RootWindow = 2;
    private const int StyleIndex = -16;
    private const int ExStyleIndex = -20;
    private const long ChildStyle = 0x40000000;
    private const long ToolWindowStyle = 0x00000080;
    private const int CloakedAttribute = 14;

    private const uint DefaultToNearest = 2;
    private const uint DefaultToPrimary = 1;

    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private const uint ShowMinimisedNoActivate = 7;

    // A window moved this many times is fighting to stay there, so it is left be rather than flicker.
    private const int MostMoves = 10;

    // The desktop and the taskbars, which belong on every screen.
    private static readonly HashSet<string> ShellClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"];

    // Windows calls the hooks with no object, so they reach the one guard through this.
    private static PanelGuard? _current;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HashSet<nint> _waiting = [];
    private readonly Dictionary<nint, int> _moves = [];
    private readonly HashSet<nint> _refused = [];
    private readonly List<nint> _hooks = [];
    private bool _drainQueued;
    private bool _cursorDue;

    private ScreenRect? _panel;
    private ScreenRect _panelWork;
    private ScreenRect _monitor;
    private ScreenRect _monitorWork;
    private ScreenRect? _cursorArea;
    private ScreenRect? _clipped;

    // How far Windows' workspace coordinates, used for a minimised window's place, are from screen pixels.
    private int _workspaceX;
    private int _workspaceY;

    /// <summary>Each second while the stats are on the panel: moves anything else off it.</summary>
    public void Keep(ScreenRect panel)
    {
        if (_panel != panel)
        {
            _panel = panel;
            _moves.Clear();
            _refused.Clear();
            ReadScreens(panel);
        }

        Hook();
        Sweep();
        KeepCursor();
    }

    /// <summary>Stops watching and gives the mouse back, as when undocked or switched off.</summary>
    public void Stop()
    {
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }

        _hooks.Clear();
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }

        if (_clipped is { } area && GetClipCursor(out var clip) && ToScreenRect(clip) == area)
        {
            ClipCursor(null);
        }

        _clipped = null;
        _panel = null;
    }

    public void Dispose() => Stop();

    private void ReadScreens(ScreenRect panel)
    {
        var monitors = AllyScreen.MonitorBounds();
        _cursorArea = ScreenStatsLayout.CursorArea(monitors, panel);
        _panelWork = WorkArea(panel) ?? panel;

        _monitor = monitors.Count > 0 ? ScreenStatsLayout.MainMonitor(monitors) : MonitorOf(MonitorFromPoint(new Point(0, 0), DefaultToPrimary)).Bounds;
        _monitorWork = WorkArea(_monitor) ?? _monitor;

        var primary = MonitorOf(MonitorFromPoint(new Point(0, 0), DefaultToPrimary));
        _workspaceX = primary.Work.X - primary.Bounds.X;
        _workspaceY = primary.Work.Y - primary.Bounds.Y;

        Program.Log($"Stats screen: keeping other windows and the mouse off the Ally's screen ({panel.Width}x{panel.Height} at {panel.X},{panel.Y}); they go to the monitor at {_monitor.X},{_monitor.Y}");
    }

    private void Hook()
    {
        _current = this;
        if (_hooks.Count > 0)
        {
            return;
        }

        foreach (var kind in (uint[])[EventSystemForeground, EventObjectShow, EventObjectLocationChange])
        {
            var hook = SetWinEventHook(kind, kind, 0, &OnEvent, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (hook != 0)
            {
                _hooks.Add(hook);
            }
        }

        if (_hooks.Count < 3)
        {
            Program.Log("Stats screen: Windows would not report window changes, so windows are moved off the Ally's screen once a second only");
        }
    }

    /// <summary>Called by Windows on this thread, as it takes messages, for each window shown, moved or brought to the front.</summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_current is not { } guard || window == 0 || objectId != ObjectWindow || childId != 0)
        {
            return;
        }

        guard._waiting.Add(window);
        guard._cursorDue |= kind == EventSystemForeground;

        // Many moves arrive at once while a window is dragged or the screens change, so they are looked at together.
        if (!guard._drainQueued)
        {
            guard._drainQueued = true;
            guard._dispatcher.BeginInvoke(DispatcherPriority.Background, guard.Drain);
        }
    }

    private void Drain()
    {
        _drainQueued = false;
        var windows = _waiting.ToList();
        _waiting.Clear();
        foreach (var window in windows)
        {
            Check(window);
        }

        if (_cursorDue)
        {
            _cursorDue = false;
            KeepCursor();
        }
    }

    private void Sweep()
    {
        var windows = new List<nint>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        foreach (var window in windows)
        {
            Check(window);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint list)
    {
        ((List<nint>)GCHandle.FromIntPtr(list).Target!).Add(window);
        return 1;
    }

    /// <summary>Moves the window to the monitor when it is a program's own window on the panel.</summary>
    private void Check(nint window)
    {
        if (_panel is not { } panel || _refused.Contains(window) || !IsWindowVisible(window) || GetAncestor(window, RootWindow) != window)
        {
            return;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId
            || (GetWindowLongPtrW(window, StyleIndex) & ChildStyle) != 0
            || (GetWindowLongPtrW(window, ExStyleIndex) & ToolWindowStyle) != 0
            || IsCloaked(window)
            || ShellClasses.Contains(ClassName(window)))
        {
            return;
        }

        if (IsIconic(window))
        {
            MoveMinimised(window, panel, processId);
            return;
        }

        if (!GetWindowRect(window, out var rect))
        {
            return;
        }

        var now = ToScreenRect(rect);
        if (!ScreenStatsLayout.IsOn(now, panel))
        {
            return;
        }

        // Maximised, it fills the monitor's free area; filling the whole panel, as a full screen game does, the whole monitor.
        var to = IsZoomed(window) ? ScreenStatsLayout.Fill(now, _panelWork, _monitorWork)
            : now.Covers(panel) ? ScreenStatsLayout.Fill(now, panel, _monitor)
            : ScreenStatsLayout.MoveOff(now, panel, _monitorWork);

        Moved(window, processId, SetWindowPos(window, 0, to.X, to.Y, to.Width, to.Height, NoZOrder | NoActivate | NoOwnerZOrder));
    }

    /// <summary>A minimised window shows nothing, but would come back on the panel, so where it comes back to is moved.</summary>
    private void MoveMinimised(nint window, ScreenRect panel, uint processId)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        if (!GetWindowPlacement(window, &placement))
        {
            return;
        }

        var normal = ToScreenRect(placement.Normal) with
        {
            X = placement.Normal.Left + _workspaceX,
            Y = placement.Normal.Top + _workspaceY
        };

        if (!ScreenStatsLayout.IsOn(normal, panel))
        {
            return;
        }

        var to = ScreenStatsLayout.MoveOff(normal, panel, _monitorWork);
        placement.Normal = new Rect
        {
            Left = to.X - _workspaceX,
            Top = to.Y - _workspaceY,
            Right = to.Right - _workspaceX,
            Bottom = to.Bottom - _workspaceY
        };
        placement.ShowCommand = ShowMinimisedNoActivate;
        Moved(window, processId, SetWindowPlacement(window, &placement));
    }

    private void Moved(nint window, uint processId, bool done)
    {
        var name = Path.GetFileName(Native.ProcessPath((int)processId)) ?? $"process {processId}";
        if (!done)
        {
            // A window of a program with more rights than this one cannot be moved. Said once.
            _refused.Add(window);
            Program.Log($"Stats screen: Windows would not move a window of {name} off the Ally's screen (error {Marshal.GetLastPInvokeError()})");
            return;
        }

        var moves = _moves[window] = _moves.GetValueOrDefault(window) + 1;
        if (moves == 1)
        {
            Program.Log($"Stats screen: moved a window of {name} off the Ally's screen");
        }
        else if (moves >= MostMoves)
        {
            _refused.Add(window);
            Program.Log($"Stats screen: a window of {name} keeps going back to the Ally's screen, so it is left there");
        }
    }

    /// <summary>
    /// Keeps the mouse on the monitors. Windows lets go of it now and then, so it is kept again. A game that
    /// keeps the mouse inside its own window is left as it is.
    /// </summary>
    private void KeepCursor()
    {
        if (_panel is not { } panel || _cursorArea is not { } area || !GetClipCursor(out var clip) || !ToScreenRect(clip).Overlaps(panel))
        {
            return;
        }

        var rect = new Rect { Left = area.X, Top = area.Y, Right = area.Right, Bottom = area.Bottom };
        if (ClipCursor(&rect))
        {
            _clipped = area;
        }
    }

    private static bool IsCloaked(nint window)
    {
        int cloaked;
        return DwmGetWindowAttribute(window, CloakedAttribute, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string ClassName(nint window)
    {
        var buffer = stackalloc char[256];
        var length = GetClassNameW(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static ScreenRect? WorkArea(ScreenRect screen) =>
        MonitorFromPoint(new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2), DefaultToNearest) is var monitor && monitor != 0
            ? MonitorOf(monitor).Work
            : null;

    private static (ScreenRect Bounds, ScreenRect Work) MonitorOf(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        return GetMonitorInfoW(monitor, &info) ? (ToScreenRect(info.Monitor), ToScreenRect(info.Work)) : (default, default);
    }

    private static ScreenRect ToScreenRect(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>WINDOWPLACEMENT: a window's state and where it goes when not minimised or maximised.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public Rect Normal;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial long GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(nint window, char* name, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, WindowPlacement* placement);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint window, WindowPlacement* placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClipCursor(out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClipCursor(Rect* rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
}
