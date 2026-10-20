using System.Runtime.InteropServices;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Whether Xbox Game Bar is open, read from Windows' own GameBar.IsInputRedirected: true while Game Bar has the
/// controller and keyboard. Game Bar draws in a layer of its own, so neither the window in front nor the list
/// of windows shows it (both checked on the Ally on 2026-10-18), and GameBar.Visible only speaks for the
/// calling app's own view. Called through the class's activation factory, so the library needs no WinRT
/// projection for one value.
/// </summary>
internal static partial class GameBarState
{
    private const string ClassName = "Windows.Gaming.UI.GameBar";

    // IGameBarStatics.
    private static readonly Guid StaticsId = new("1DB9A292-CC78-4173-BE45-B61E67283EA7");

    // After IUnknown (3) and IInspectable (3): add and remove VisibilityChanged, add and remove
    // IsInputRedirectedChanged, then get_Visible (10) and get_IsInputRedirected (11).
    private const int IsInputRedirectedSlot = 11;

    /// <summary>Whether Game Bar has the input now, or null when Windows would not say.</summary>
    public static unsafe bool? IsOpen()
    {
        // 1 is RO_INIT_MULTITHREADED. S_FALSE (already initialised) and a different mode already set are both fine.
        _ = RoInitialize(1);

        if (WindowsCreateString(ClassName, ClassName.Length, out var name) < 0)
        {
            return null;
        }

        nint factory;
        try
        {
            var id = StaticsId;
            if (RoGetActivationFactory(name, ref id, out factory) < 0 || factory == 0)
            {
                return null;
            }
        }
        finally
        {
            WindowsDeleteString(name);
        }

        try
        {
            var get = (delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)factory)[IsInputRedirectedSlot];
            byte redirected;
            return get(factory, &redirected) < 0 ? null : redirected != 0;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string text, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, ref Guid interfaceId, out nint factory);
}
