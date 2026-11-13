using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace HandheldOptimiser.PowerService;

/// <summary>
/// The service's pipe: who may connect, reading a request, sending it to the part that answers it, and
/// writing back the status with what each part holds added to it.
/// </summary>
internal sealed partial class AutoTdpService
{
    // ----- Requests from the library -----

    private async Task ServeAsync(CancellationToken cancel)
    {
        // The first instance claims the name, so a program that took it first cannot pose as this service.
        NamedPipeServerStream claimed;
        try
        {
            claimed = CreatePipe(first: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Another program holds the name. Nothing can reach this service, so it ends.
            Log.Write($"Could not open the pipe: {ex.Message}");
            _ = Task.Run(EndSelf);
            return;
        }

        var listeners = new List<Task> { ListenAsync(claimed, cancel) };
        for (var i = 1; i < PipeInstances; i++)
        {
            listeners.Add(ListenAsync(null, cancel));
        }

        await Task.WhenAll(listeners).ConfigureAwait(false);
    }

    /// <summary>Answers one connection after another on one pipe instance, until the service ends. Never throws.</summary>
    private async Task ListenAsync(NamedPipeServerStream? pipe, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream current;
            try
            {
                current = pipe ?? CreatePipe(first: false);
                pipe = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Could not open another pipe instance: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await using (current)
                {
                    await current.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(RequestTimeout);
                    await AnswerAsync(current, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // The library went away mid-request.
            }
            catch (Exception ex)
            {
                // Whatever one client did, the pipe stays open for the next.
                Log.Write($"A request failed: {ex.Message}");
            }
        }
    }

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var request = AutoTdpProtocol.Read<AutoTdpRequest>(await AutoTdpRules.ReadLineAsync(pipe, cancel).ConfigureAwait(false));
        var client = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : 0;

        AutoTdpStatus status;
        if (request is not null && _routes.TryGetValue(request.Command ?? string.Empty, out var route))
        {
            // Answered outside the gate: each of these writes to the controller, the firmware or Windows,
            // or waits for a program run as the player, and the engine holds none of it. Only what the
            // engine last said is read under the gate.
            AutoTdpStatus last;
            string? error;
            lock (_gate)
            {
                last = _last;
                error = _error;
                if (route.KeepsAwake)
                {
                    _idleSince = DateTime.UtcNow;
                }
            }

            status = route.Answer(request, last, error);
        }
        else
        {
            lock (_gate)
            {
                status = request?.Command switch
                {
                    AutoTdpProtocol.Start => Begin(request, client),
                    AutoTdpProtocol.Update => Update(request, client),
                    AutoTdpProtocol.Stop => End(),
                    AutoTdpProtocol.SetOverlay => SetOverlay(request),
                    AutoTdpProtocol.SetScreenStats => SetScreenStats(request),
                    AutoTdpProtocol.SetMode => SetMode(request, client),
                    AutoTdpProtocol.Overlay => OverlayStatus(request),
                    _ => _error is not null ? new AutoTdpStatus(false, Error: _error) : _last
                };
            }
        }

        // Each part adds what it holds. Read each time, as the player or another program can change it.
        status = AllyControlsHost.AddTo(_controls, status);
        status = CpuBoost.AddTo(status);
        status = _overlay.AddTo(status);
        status = _screenStats.AddTo(status);
        status = _engine.AddTo(status);
        status = _gpuMemory.AddTo(status);
        status = _batteryCare.AddTo(status);
        status = _radeon.AddTo(status);
        await writer.WriteLineAsync(AutoTdpProtocol.Write(status).AsMemory(), cancel).ConfigureAwait(false);

        // Restarted only once the widget has its answer.
        if (request?.Command == AutoTdpProtocol.Restart)
        {
            RestartWindows();
        }
    }

    /// <summary>
    /// The requests that are not the engine's own: the part of the service that answers each, and
    /// whether it counts as the service being used, which puts off its ending by itself. A request that
    /// is not here is the engine's, and is answered under the gate. The parts are read as a request
    /// arrives, not as this is made, since some are only made once the firmware has been opened.
    /// </summary>
    private Dictionary<string, RequestRoute> Routes()
    {
        RequestRoute lighting = new((request, last, error) => AllyControlsHost.AnswerLighting(_controls, request, last, error));
        RequestRoute gpuMemory = new((request, last, _) => _gpuMemory.Answer(request, last), KeepsAwake: true);

        return new(StringComparer.Ordinal)
        {
            [AutoTdpProtocol.SetControls] = new((request, last, error) => AllyControlsHost.AnswerMode(_controls, request, last, error)),
            [AutoTdpProtocol.SetPaddle] = new((request, last, error) => AllyControlsHost.AnswerPaddle(_controls, request, last, error)),
            [AutoTdpProtocol.SetLighting] = lighting,
            [AutoTdpProtocol.SetLightingColour] = lighting,
            [AutoTdpProtocol.SetBoost] = new((request, last, _) => CpuBoost.Answer(request, last)),
            [AutoTdpProtocol.SetBatteryCare] = new((request, last, _) => _batteryCare.Answer(request, last), KeepsAwake: true),
            [AutoTdpProtocol.SetRadeon] = new((request, last, _) => _radeon.Answer(request, last), KeepsAwake: true),
            [AutoTdpProtocol.StartMusic] = new((_, last, _) => StartMusic(last)),
            [AutoTdpProtocol.GpuMemoryRequest] = gpuMemory,
            [AutoTdpProtocol.SetGpuMemory] = gpuMemory,

            // Answered like a read of the GPU memory. Windows is restarted once the widget has its answer.
            [AutoTdpProtocol.Restart] = gpuMemory
        };
    }

    private static void RestartWindows()
    {
        Log.Write("Restarting Windows for a GPU memory change, at the player's request");
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"Windows would not restart: {ex.Message}");
        }
    }

    /// <summary>
    /// The system and administrators own the pipe; the signed-in player may only talk to it, from the
    /// library or from the Game Bar widget. The widget runs in an app sandbox, at low integrity, so the pipe
    /// lets in that one app package (not every app) and is labelled so a low-integrity client may write.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool first)
    {
        // A client opening it to read and write is checked for creating an instance as well, which generic
        // write includes for a pipe. With several instances allowed, that also lets the player's programs
        // add an instance of their own while one is free, so the library checks that the end it reached
        // belongs to this service's process before it trusts an answer.
        var sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";
        if (WidgetSid() is { } widget)
        {
            sddl += $"(A;;GRGW;;;{widget})S:(ML;;NW;;;LW)";
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, 0))
        {
            throw new System.ComponentModel.Win32Exception();
        }

        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + AutoTdpProtocol.PipeName,
                PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0),
                PipeTypeByte | PipeRejectRemoteClients, PipeInstances, 0, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException($"The pipe could not be created ({new System.ComponentModel.Win32Exception(error).Message})");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>The Game Bar widget's app package as a security identifier, or null when Windows cannot work it out.</summary>
    private static string? WidgetSid()
    {
        if (DeriveAppContainerSidFromAppContainerName(AutoTdpProtocol.WidgetPackage, out var sid) != 0)
        {
            return null;
        }

        try
        {
            return new SecurityIdentifier(sid).Value;
        }
        finally
        {
            FreeSid(sid);
        }
    }

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out nint descriptor, nint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
