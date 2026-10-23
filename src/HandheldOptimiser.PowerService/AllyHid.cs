using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The ROG Ally's controller over HID: its button reports in, and its settings out as feature reports.
/// The Ally and the Ally X are looked for, by the IDs in <see cref="AllyControllers"/>. Linux's hid-asus driver
/// treats the two the same for the reports used here.
///
/// The input collection and the feature collections are opened as separate handles. A blocking read holds
/// its own handle, so a report can be sent from another thread while one thread waits for a button.
/// </summary>
internal sealed partial class AllyHid : IControllerBackend
{
    private const ushort VendorPage = 0xFF31;

    /// <summary>The collection the button reports arrive on.</summary>
    private const ushort InputUsage = 0x76;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;

    private const int HidpStatusSuccess = 0x00110000;
    private const int HidpFeature = 2;

    private readonly SafeFileHandle _input;
    private readonly int _inputLength;
    private readonly (SafeFileHandle Handle, int Length)[] _features;
    private readonly string _description;
    private int _disposed;

    private AllyHid(SafeFileHandle input, int inputLength, (SafeFileHandle Handle, int Length)[] features, string description)
    {
        _input = input;
        _inputLength = inputLength;
        _features = features;
        _description = description;
    }

    /// <summary>
    /// The controller, or null when there is no Ally controller, or it has no collection to
    /// read buttons from or none to send settings to.
    /// </summary>
    public static AllyHid? Open()
    {
        var collections = FindCollections();
        if (collections.Count == 0)
        {
            return null;
        }

        var description = string.Join(", ", collections.Select(Describe));
        var vendor = collections.Where(c => c.UsagePage == VendorPage).ToList();
        var input = vendor.FirstOrDefault(c => c.Usage == InputUsage && c.InputLength > 0);
        var features = vendor.Where(c => c.HasCommandReport).ToList();
        if (features.Count == 0)
        {
            // The caps should name report 0x5A, but if this firmware's do not, the collections the settings
            // are known to go to are the vendor ones with a 64 byte feature report.
            features = vendor.Where(c => c.FeatureLength == AllyCommands.FeatureLength).ToList();
            if (features.Count > 0)
            {
                Log.Write("No controller collection names feature report 0x5A, so every vendor collection with a 64 byte feature report is used.");
            }
        }

        if (input is null || features.Count == 0)
        {
            Log.Write($"The Ally controller was found but has no usable collections: {description}");
            return null;
        }

        var opened = new List<(SafeFileHandle Handle, int Length)>();
        var inputHandle = CreateFile(input.Path, GenericRead, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (inputHandle.IsInvalid)
        {
            Log.Write($"The Ally controller's input collection did not open (error {Marshal.GetLastPInvokeError()}).");
            inputHandle.Dispose();
            return null;
        }

        foreach (var feature in features)
        {
            var handle = CreateFile(feature.Path, GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                Log.Write($"The Ally controller's collection 0x{feature.Usage:X2} did not open for writing (error {Marshal.GetLastPInvokeError()}).");
                handle.Dispose();
                continue;
            }

            opened.Add((handle, feature.FeatureLength));
        }

        if (opened.Count == 0)
        {
            inputHandle.Dispose();
            return null;
        }

        return new AllyHid(inputHandle, input.InputLength, [.. opened], description);
    }

    /// <summary>
    /// Sends a feature report, such as one from <see cref="AllyCommands"/>, to every feature collection,
    /// padded with zeros to the collection's feature report length.
    /// </summary>
    /// <returns>True when at least one collection took it.</returns>
    public bool Send(byte[] report)
    {
        var sent = false;
        foreach (var (handle, length) in _features)
        {
            // A report longer than the collection's would be cut short, which would change what it says.
            if (report.Length == 0 || report.Length > length)
            {
                continue;
            }

            var buffer = report;
            if (report.Length < length)
            {
                buffer = new byte[length];
                report.CopyTo(buffer, 0);
            }

            try
            {
                if (HidD_SetFeature(handle, buffer, (uint)buffer.Length))
                {
                    sent = true;
                }
                else
                {
                    Log.Write($"The Ally controller refused feature report 0x{report[0]:X2} (error {Marshal.GetLastPInvokeError()}).");
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed on another thread while sending: the controller is being let go.
            }
        }

        return sent;
    }

    /// <summary>
    /// Waits for the next input report and returns it, its first byte the report ID.
    /// </summary>
    /// <returns>Null once the controller is gone, or once this is disposed.</returns>
    public unsafe byte[]? Read()
    {
        var report = new byte[_inputLength];
        uint read;
        try
        {
            fixed (byte* buffer = report)
            {
                if (!ReadFile(_input, buffer, (uint)report.Length, out read, 0))
                {
                    return null;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        if (read == 0)
        {
            return null;
        }

        return read == report.Length ? report : report[..(int)read];
    }

    /// <summary>The controller's collections, for the log.</summary>
    public string Describe() => _description;

    /// <inheritdoc/>
    public bool Wake() => Send(AllyCommands.Wake());

    /// <inheritdoc/>
    public bool SetMode(ControllerMode mode) => Send(AllyCommands.Mode(mode));

    /// <inheritdoc/>
    public bool TryRead(out AllyButton button)
    {
        var report = Read();
        button = report is null ? AllyButton.None : AllyButtons.Parse(report);
        return report is not null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A read waiting for a button would otherwise keep waiting, and hold its thread, until one is pressed.
        CancelIoEx(_input, 0);
        _input.Dispose();
        foreach (var (handle, _) in _features)
        {
            handle.Dispose();
        }
    }

    /// <summary>One HID collection of the controller, as its caps describe it.</summary>
    private sealed record Collection(string Path, ushort UsagePage, ushort Usage, int InputLength, int FeatureLength,
        bool HasCommandReport);

    private static string Describe(Collection c) =>
        $"0x{c.UsagePage:X4}/0x{c.Usage:X2} (input {c.InputLength}, feature {c.FeatureLength}{(c.HasCommandReport ? ", 0x5A" : "")})";

    /// <summary>Every present HID interface of the controller whose caps could be read.</summary>
    private static unsafe List<Collection> FindCollections()
    {
        var found = new List<Collection>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1)
        {
            Log.Write($"The HID devices could not be listed (error {Marshal.GetLastPInvokeError()}).");
            return found;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterfaceData { Size = sizeof(DeviceInterfaceData) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref hidGuid, index, ref data))
                {
                    break;
                }

                if (InterfacePath(set, ref data) is { } path &&
                    AllyControllers.Matches(path) &&
                    Probe(path) is { } collection)
                {
                    found.Add(collection);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static string? InterfacePath(nint set, ref DeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var size, 0);
        if (size == 0)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W's cbSize is the size of the fixed part, a DWORD and one
            // character padded to 8 on x64, not the size of the buffer.
            Marshal.WriteInt32(detail, 8);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, 0))
            {
                return null;
            }

            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>Reads a collection's caps. It is opened with no access, which is enough for them.</summary>
    private static Collection? Probe(string path)
    {
        using var handle = CreateFile(path, 0, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess)
            {
                return null;
            }

            var hasCommandReport = HasFeatureReport(preparsed, caps.NumberFeatureButtonCaps, buttons: true) ||
                HasFeatureReport(preparsed, caps.NumberFeatureValueCaps, buttons: false);
            return new Collection(path, caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                caps.FeatureReportByteLength, hasCommandReport);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Whether the collection's feature button or value caps include report ID 0x5A.</summary>
    private static unsafe bool HasFeatureReport(nint preparsed, ushort count, bool buttons)
    {
        if (count == 0)
        {
            return false;
        }

        var caps = new ReportCaps[count];
        var length = count;
        fixed (ReportCaps* list = caps)
        {
            var status = buttons
                ? HidP_GetButtonCaps(HidpFeature, list, ref length, preparsed)
                : HidP_GetValueCaps(HidpFeature, list, ref length, preparsed);
            if (status != HidpStatusSuccess)
            {
                return false;
            }
        }

        for (var i = 0; i < length && i < caps.Length; i++)
        {
            if (caps[i].ReportId == AllyCommands.ReportId)
            {
                return true;
            }
        }

        return false;
    }

    // SP_DEVICE_INTERFACE_DATA: 32 bytes on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    // HIDP_CAPS: five USHORTs, 17 reserved USHORTs, then ten USHORT counts, 64 bytes in all.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// One HIDP_BUTTON_CAPS or HIDP_VALUE_CAPS. Both are 72 bytes on x64 and both begin with a USHORT usage
    /// page then the UCHAR report ID, which is the only field read, so one layout serves for both.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct ReportCaps
    {
        [FieldOffset(2)]
        public byte ReportId;
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_SetFeature(SafeFileHandle device, byte[] report, uint length);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll")]
    private static unsafe partial int HidP_GetButtonCaps(int reportType, ReportCaps* caps, ref ushort length, nint preparsed);

    [LibraryImport("hid.dll")]
    private static unsafe partial int HidP_GetValueCaps(int reportType, ReportCaps* caps, ref ushort length, nint preparsed);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint deviceInfo, ref Guid interfaceClass, uint index,
        ref DeviceInterfaceData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail,
        uint detailSize, out uint requiredSize, nint deviceInfo);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(SafeFileHandle file, byte* buffer, uint size, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
