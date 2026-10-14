using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// Counts each program's frames from the events Windows' graphics stack writes for every present, the way
/// PresentMon (and so the Game Bar's and Radeon's frame counters) does. Only the few events that mark a
/// present are switched on, filtered by event number inside Windows, so it costs very little. Reading
/// them needs administrator or the system account, which is why this runs in a service.
///
/// Direct3D 10 to 12 games are counted by DXGI's present event and Direct3D 9 games by Direct3D 9's.
/// Vulkan and OpenGL games write neither, so for a program with none of those the graphics kernel's own
/// present event counts instead. Event numbers and keywords are from PresentMon's
/// PresentData/ETW headers (github.com/GameTechDev/PresentMon).
/// </summary>
internal sealed unsafe partial class FrameCounter : IDisposable
{
    private const string SessionName = "HandheldOptimiser AutoTdp";

    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    // DXGI Present_Start and PresentMultiplaneOverlay_Start, Direct3D 9 Present_Start, DxgKrnl Present_Info.
    private const ushort DxgiPresent = 0x2A;
    private const ushort DxgiOverlayPresent = 0x37;
    private const ushort D3D9Present = 0x01;
    private const ushort KernelPresent = 0xB8;

    // The keywords those events carry, less the channel bits PresentMon also leaves out. Before Windows 11
    // the kernel's present events have no Present keyword.
    private const ulong RuntimeKeywords = 0x8000000000000002;
    private const ulong KernelKeywords = 0x0000000008000001;
    private const ulong KernelKeywordsBeforeWindows11 = 0x0000000000000001;

    private const uint RealTimeMode = 0x00000100;
    private const uint TracedGuid = 0x00020000;
    private const uint ControlStop = 1;
    private const uint EnableProvider = 1;
    private const uint ProcessTraceRealTime = 0x00000100;
    private const uint ProcessTraceEventRecord = 0x10000000;
    private const uint FilterTypeEventId = 0x80000200;
    private const uint IgnoreKeyword0 = 0x10;
    private const byte LevelVerbose = 5;
    private const uint AlreadyExists = 183;

    // EVENT_TRACE_PROPERTIES on x64, followed by room for the session name.
    private const int PropertiesSize = 120;
    private const int PropertiesBuffer = PropertiesSize + 2 * 256;

    // EVENT_TRACE_LOGFILEW on x64, and the fields set in it.
    private const int LogFileSize = 448;
    private const int LogFileLoggerName = 8;
    private const int LogFileProcessTraceMode = 28;
    private const int LogFileEventRecordCallback = 424;

    private static readonly ulong InvalidTrace = ulong.MaxValue;

    // Events arrive on the processing thread through a static callback, so the one counter running is kept here.
    private static FrameCounter? _running;

    private readonly object _gate = new();
    private Dictionary<int, Counts> _counts = [];
    private DateTime _since = DateTime.UtcNow;

    private ulong _session;
    private ulong _trace = InvalidTrace;
    private Thread? _thread;
    private nint _name;

    private FrameCounter()
    {
    }

    /// <summary>Starts counting.</summary>
    /// <exception cref="Win32Exception">Windows would not start the trace, as when not running as administrator.</exception>
    public static FrameCounter Start()
    {
        var counter = new FrameCounter();
        try
        {
            counter.StartSession();
            _running = counter;
            counter.StartReading();
            return counter;
        }
        catch
        {
            counter.Dispose();
            throw;
        }
    }

    /// <summary>Each program's frames a second since the last call, by process id.</summary>
    public Dictionary<int, double> Take()
    {
        Dictionary<int, Counts> counts;
        DateTime since;
        lock (_gate)
        {
            counts = _counts;
            since = _since;
            _counts = [];
            _since = DateTime.UtcNow;
        }

        var seconds = Math.Max((DateTime.UtcNow - since).TotalSeconds, 0.001);

        // The runtime's count where there is one: the kernel's event can also come from other paths for the same frame.
        return counts.ToDictionary(pair => pair.Key, pair => (pair.Value.Runtime > 0 ? pair.Value.Runtime : pair.Value.Kernel) / seconds);
    }

    private void StartSession()
    {
        // One left behind by a service that was ended without cleaning up would block a new one.
        StopSession();

        var properties = NewProperties();
        fixed (byte* buffer = properties)
        {
            var error = StartTrace(out _session, SessionName, buffer);
            if (error == AlreadyExists)
            {
                StopSession();
                properties = NewProperties();
                fixed (byte* again = properties)
                {
                    error = StartTrace(out _session, SessionName, again);
                }
            }

            if (error != 0)
            {
                throw new Win32Exception((int)error, "Could not start the frame counting trace");
            }
        }

        Enable(Dxgi, RuntimeKeywords, [DxgiPresent, DxgiOverlayPresent]);
        Enable(D3D9, RuntimeKeywords, [D3D9Present]);
        Enable(DxgKrnl, Environment.OSVersion.Version.Build >= 22000 ? KernelKeywords : KernelKeywordsBeforeWindows11, [KernelPresent]);
    }

    private static byte[] NewProperties()
    {
        var properties = new byte[PropertiesBuffer];
        fixed (byte* p = properties)
        {
            *(uint*)(p + 0) = PropertiesBuffer;   // Wnode.BufferSize
            *(uint*)(p + 40) = 1;                 // Wnode.ClientContext: QueryPerformanceCounter time stamps
            *(uint*)(p + 44) = TracedGuid;        // Wnode.Flags
            *(uint*)(p + 64) = RealTimeMode;      // LogFileMode
            *(uint*)(p + 68) = 1;                 // FlushTimer, in seconds
            *(uint*)(p + 116) = PropertiesSize;   // LoggerNameOffset
        }

        return properties;
    }

    private void Enable(Guid provider, ulong keywords, ushort[] events)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, then the event numbers.
        var filterSize = 4 + 2 * events.Length;
        var filter = stackalloc byte[filterSize];
        filter[0] = 1;
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)events.Length;
        for (var i = 0; i < events.Length; i++)
        {
            *(ushort*)(filter + 4 + 2 * i) = events[i];
        }

        // EVENT_FILTER_DESCRIPTOR: Ptr, Size, Type.
        var descriptor = stackalloc byte[16];
        *(ulong*)descriptor = (ulong)filter;
        *(uint*)(descriptor + 8) = (uint)filterSize;
        *(uint*)(descriptor + 12) = FilterTypeEventId;

        // ENABLE_TRACE_PARAMETERS version 2: Version, EnableProperty, ControlFlags, SourceId, then
        // EnableFilterDesc and FilterDescCount after padding to 8 bytes.
        var parameters = stackalloc byte[48];
        new Span<byte>(parameters, 48).Clear();
        *(uint*)parameters = 2;
        *(uint*)(parameters + 4) = IgnoreKeyword0;
        *(ulong*)(parameters + 32) = (ulong)descriptor;
        *(uint*)(parameters + 40) = 1;

        var error = EnableTraceEx2(_session, &provider, EnableProvider, LevelVerbose, keywords, keywords, 0, parameters);
        if (error != 0)
        {
            throw new Win32Exception((int)error, $"Could not switch on the present events of {provider}");
        }
    }

    private void StartReading()
    {
        _name = Marshal.StringToHGlobalUni(SessionName);
        var logFile = stackalloc byte[LogFileSize];
        new Span<byte>(logFile, LogFileSize).Clear();
        *(nint*)(logFile + LogFileLoggerName) = _name;
        *(uint*)(logFile + LogFileProcessTraceMode) = ProcessTraceRealTime | ProcessTraceEventRecord;
        *(nint*)(logFile + LogFileEventRecordCallback) = (nint)(delegate* unmanaged<byte*, void>)&OnEvent;

        _trace = OpenTrace(logFile);
        if (_trace == InvalidTrace)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the frame counting trace");
        }

        var trace = _trace;
        _thread = new Thread(() =>
        {
            // Returns once the session stops or the trace is closed.
            var handle = trace;
            ProcessTrace(&handle, 1, 0, 0);
        })
        {
            IsBackground = true,
            Name = "Frame counting"
        };
        _thread.Start();
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        if (_running is not { } counter)
        {
            return;
        }

        // EVENT_RECORD starts with EVENT_HEADER: ProcessId at 12, ProviderId at 24, EventDescriptor.Id at 40.
        var processId = *(int*)(record + 12);
        var provider = *(Guid*)(record + 24);
        var id = *(ushort*)(record + 40);

        var runtime = (provider == Dxgi && id is DxgiPresent or DxgiOverlayPresent) || (provider == D3D9 && id == D3D9Present);
        var kernel = provider == DxgKrnl && id == KernelPresent;
        if (!runtime && !kernel)
        {
            return;
        }

        lock (counter._gate)
        {
            ref var counts = ref CollectionsMarshal.GetValueRefOrAddDefault(counter._counts, processId, out _);
            if (runtime)
            {
                counts.Runtime++;
            }
            else
            {
                counts.Kernel++;
            }
        }
    }

    private void StopSession()
    {
        var properties = NewProperties();
        fixed (byte* buffer = properties)
        {
            ControlTrace(0, SessionName, buffer, ControlStop);
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_running, this))
        {
            _running = null;
        }

        StopSession();
        if (_trace != InvalidTrace)
        {
            CloseTrace(_trace);
            _trace = InvalidTrace;
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        if (_name != 0)
        {
            Marshal.FreeHGlobal(_name);
            _name = 0;
        }
    }

    private struct Counts
    {
        public int Runtime;
        public int Kernel;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTrace(out ulong session, string name, byte* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong session, string name, byte* properties, uint control);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong session, Guid* provider, uint control, byte level, ulong matchAny,
        ulong matchAll, uint timeout, byte* parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static partial ulong OpenTrace(byte* logFile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong trace);
}
