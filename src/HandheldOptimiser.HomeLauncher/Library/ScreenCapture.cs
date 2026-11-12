using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One picture of a screen: 32-bit pixels, blue first, top row first.</summary>
internal sealed record ScreenPicture(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>
/// Copies a screen through DXGI's desktop duplication, which hands over the picture the graphics card
/// is sending to the screen. A GDI copy of the desktop cannot see a game that owns the screen (exclusive
/// full screen) and can miss a game drawn on its own hardware plane, so either can come out black.
/// Duplication sees both. It is written against the COM tables by hand to keep the Windows SDK's
/// projection, about 25 MB, out of the library.
/// </summary>
internal static unsafe partial class ScreenCapture
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int FormatBgra = 87;
    private const int RotationUnspecified = 0;
    private const int RotationIdentity = 1;
    private const uint UsageStaging = 3;
    private const uint CpuAccessRead = 0x20000;
    private const uint MapRead = 1;
    private const uint SdkVersion = 7;

    // How many frames are asked for, and how long each is waited for. The first frame handed over can
    // be a pointer move with no picture behind it, so a few are tried before settling for what there is.
    private const int Attempts = 6;
    private const uint FrameWaitMs = 100;

    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Copies the screen behind a monitor handle.</summary>
    /// <param name="monitor">The HMONITOR of the screen to copy.</param>
    /// <param name="why">Why there is no picture, for the log, when null is returned.</param>
    public static ScreenPicture? Capture(nint monitor, out string? why)
    {
        why = null;
        nint factory = 0, adapter = 0, output = 0, output1 = 0, device = 0, context = 0, duplication = 0, staging = 0;
        try
        {
            var factoryId = Factory1Id;
            var result = CreateDXGIFactory1(&factoryId, &factory);
            if (result < 0)
            {
                why = $"no DXGI factory (0x{result:X8})";
                return null;
            }

            if (!FindOutput(factory, monitor, out adapter, out output, out var rotation))
            {
                why = "the screen is not one a graphics card reports";
                return null;
            }

            // A screen Windows turns round arrives unturned. That is rare on a handheld's own panel and
            // not worth turning by hand, so the GDI copy, which is already the right way up, takes it.
            if (rotation is not (RotationIdentity or RotationUnspecified))
            {
                why = "the screen is rotated";
                return null;
            }

            result = D3D11CreateDevice(adapter, 0, 0, 0, null, 0, SdkVersion, &device, null, &context);
            if (result < 0)
            {
                why = $"no Direct3D device (0x{result:X8})";
                return null;
            }

            var output1Id = Output1Id;
            result = QueryInterface(output, &output1Id, &output1);
            if (result < 0)
            {
                why = $"the screen cannot be duplicated (0x{result:X8})";
                return null;
            }

            // IDXGIOutput1::DuplicateOutput.
            result = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(output1, 22))(output1, device, &duplication);
            if (result < 0)
            {
                why = $"Windows would not duplicate the screen (0x{result:X8})";
                return null;
            }

            TextureDescription description = default;
            var copied = false;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                FrameInfo frame;
                nint resource = 0;

                // IDXGIOutputDuplication::AcquireNextFrame.
                result = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Slot(duplication, 8))(
                    duplication, FrameWaitMs, &frame, &resource);
                if (result == WaitTimeout)
                {
                    continue;
                }

                if (result < 0)
                {
                    why = $"no frame arrived (0x{result:X8})";
                    return null;
                }

                try
                {
                    if (!CopyToStaging(device, context, resource, ref staging, ref description, out why))
                    {
                        return null;
                    }

                    copied = true;
                }
                finally
                {
                    Release(resource);

                    // IDXGIOutputDuplication::ReleaseFrame.
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(duplication, 14))(duplication);
                }

                // A present time means this frame carried a picture, not only a pointer move.
                if (frame.LastPresentTime != 0)
                {
                    break;
                }
            }

            if (!copied)
            {
                why = "no frame arrived in time";
                return null;
            }

            return Read(context, staging, description, out why);
        }
        finally
        {
            Release(staging);
            Release(duplication);
            Release(context);
            Release(device);
            Release(output1);
            Release(output);
            Release(adapter);
            Release(factory);
        }
    }

    /// <summary>Finds the graphics card and output that drive a monitor. The caller releases both.</summary>
    private static bool FindOutput(nint factory, nint monitor, out nint adapter, out nint output, out int rotation)
    {
        for (uint a = 0; ; a++)
        {
            nint candidate = 0;

            // IDXGIFactory1::EnumAdapters1.
            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, a, &candidate) < 0)
            {
                break;
            }

            for (uint o = 0; ; o++)
            {
                nint found = 0;

                // IDXGIAdapter::EnumOutputs.
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(candidate, 7))(candidate, o, &found) < 0)
                {
                    break;
                }

                OutputDescription description;

                // IDXGIOutput::GetDesc.
                if (((delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)Slot(found, 7))(found, &description) >= 0
                    && description.Monitor == monitor)
                {
                    adapter = candidate;
                    output = found;
                    rotation = description.Rotation;
                    return true;
                }

                Release(found);
            }

            Release(candidate);
        }

        adapter = 0;
        output = 0;
        rotation = RotationUnspecified;
        return false;
    }

    /// <summary>
    /// Copies the frame into a texture the processor can read, made the first time round. The frame
    /// itself lives on the graphics card and has to be given back before the next one is asked for.
    /// </summary>
    private static bool CopyToStaging(nint device, nint context, nint resource, ref nint staging,
        ref TextureDescription description, out string? why)
    {
        why = null;
        nint texture = 0;
        try
        {
            var textureId = Texture2DId;
            var result = QueryInterface(resource, &textureId, &texture);
            if (result < 0)
            {
                why = $"the frame is not a texture (0x{result:X8})";
                return false;
            }

            if (staging == 0)
            {
                TextureDescription source;

                // ID3D11Texture2D::GetDesc.
                ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)Slot(texture, 10))(texture, &source);
                if (source.Format != FormatBgra || source.Width == 0 || source.Height == 0)
                {
                    why = $"the frame is in a format that is not read here ({source.Format})";
                    return false;
                }

                source.MipLevels = 1;
                source.ArraySize = 1;
                source.SampleCount = 1;
                source.SampleQuality = 0;
                source.Usage = UsageStaging;
                source.BindFlags = 0;
                source.CpuAccessFlags = CpuAccessRead;
                source.MiscFlags = 0;

                nint made = 0;

                // ID3D11Device::CreateTexture2D.
                result = ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void*, nint*, int>)Slot(device, 5))(
                    device, &source, null, &made);
                if (result < 0)
                {
                    why = $"no texture to copy the frame into (0x{result:X8})";
                    return false;
                }

                staging = made;
                description = source;
            }

            // ID3D11DeviceContext::CopyResource.
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, staging, texture);
            return true;
        }
        finally
        {
            Release(texture);
        }
    }

    /// <summary>Reads the copied frame's pixels out, a row at a time, since a row can be padded.</summary>
    private static ScreenPicture? Read(nint context, nint staging, TextureDescription description, out string? why)
    {
        why = null;
        MappedTexture mapped;

        // ID3D11DeviceContext::Map.
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedTexture*, int>)Slot(context, 14))(
            context, staging, 0, MapRead, 0, &mapped);
        if (result < 0)
        {
            why = $"the frame could not be read (0x{result:X8})";
            return null;
        }

        try
        {
            var width = (int)description.Width;
            var height = (int)description.Height;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            fixed (byte* target = pixels)
            {
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(mapped.Data + ((long)row * mapped.RowPitch), target + ((long)row * stride), stride, stride);
                }
            }

            return new ScreenPicture(width, height, stride, pixels);
        }
        finally
        {
            // ID3D11DeviceContext::Unmap.
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, staging, 0);
        }
    }

    /// <summary>A method's address in a COM object's table.</summary>
    private static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    private static int QueryInterface(nint instance, Guid* id, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, id, result);

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
        }
    }

    /// <summary>DXGI_OUTPUT_DESC: a 32 character name, the screen's rectangle, then these.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct OutputDescription
    {
        [FieldOffset(84)] public int Rotation;
        [FieldOffset(88)] public nint Monitor;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO, of which only the present time is read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct FrameInfo
    {
        [FieldOffset(0)] public long LastPresentTime;
    }

    /// <summary>D3D11_TEXTURE2D_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    /// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedTexture
    {
        public byte* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* id, nint* factory);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        int* featureLevels, uint featureLevelCount, uint sdkVersion, nint* device, int* featureLevel, nint* context);
}
