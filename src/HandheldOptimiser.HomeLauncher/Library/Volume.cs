using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The default speakers' volume, through Windows' own audio API. Needs no administrator rights; it is the
/// same control the volume flyout moves.
/// </summary>
internal static class Volume
{
    private const int Render = 0;
    private const int Multimedia = 1;
    private const int ClsctxAll = 23;

    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>The volume from 0 to 100 and whether it is muted, or null with no speakers.</summary>
    public static (int Percent, bool Muted)? Read() => WithEndpoint(endpoint =>
    {
        endpoint.GetMasterVolumeLevelScalar(out var level);
        endpoint.GetMute(out var muted);
        return ((int)Math.Round(level * 100), muted);
    });

    /// <summary>Sets the volume and unmutes, as dragging the Windows slider does.</summary>
    /// <returns>False when there are no speakers or Windows refused.</returns>
    public static bool Set(int percent) => WithEndpoint(endpoint =>
    {
        var context = Guid.Empty;
        endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context);
        endpoint.SetMute(false, ref context);
        return true;
    }) ?? false;

    /// <returns>False when there are no speakers or Windows refused.</returns>
    public static bool SetMuted(bool muted) => WithEndpoint(endpoint =>
    {
        var context = Guid.Empty;
        endpoint.SetMute(muted, ref context);
        return true;
    }) ?? false;

    private static T? WithEndpoint<T>(Func<IAudioEndpointVolume, T> action) where T : struct
    {
        object? enumerator = null;
        IMMDevice? device = null;
        object? endpoint = null;

        try
        {
            enumerator = new MMDeviceEnumerator();
            ((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(Render, Multimedia, out device);

            var id = EndpointVolumeId;
            device.Activate(ref id, ClsctxAll, 0, out endpoint);
            return action((IAudioEndpointVolume)endpoint);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No audio device, or the audio service is stopped.
            return null;
        }
        finally
        {
            foreach (var com in new[] { endpoint, device, enumerator })
            {
                if (com is not null)
                {
                    Marshal.ReleaseComObject(com);
                }
            }
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    // Each interface lists its methods in vtable order, up to the last one called here.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid interfaceId, int context, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint notify);
        void UnregisterControlChangeNotify(nint notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
