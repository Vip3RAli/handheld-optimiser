using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.Services;

/// <summary>
/// Talks to the TDP Engine service over its pipe, as the game library and the widget do: one line of JSON
/// each way. The app only uses it for the GPU memory, which the service sets through the Ally's firmware.
/// The service ends itself when idle, so it is started when the pipe is not there; the app is elevated,
/// which lets it.
/// </summary>
internal sealed class TdpEngineClient(LogService log)
{
    private const string ServiceExeName = "HandheldOptimiser.PowerService";

    // Short, as a pipe that is not there is waited for in full, and the service is usually stopped when the
    // app starts: it ends itself when idle. A busy pipe is tried again in the loop below.
    private static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(6);

    /// <summary>Sends one request, starting the service first if need be.</summary>
    /// <returns>The answer, or null when the service never answered.</returns>
    public async Task<AutoTdpStatus?> SendAsync(AutoTdpRequest request, CancellationToken ct)
    {
        if (await TrySendAsync(request, ct) is { } answer)
        {
            return answer;
        }

        if (!StartService())
        {
            return null;
        }

        // It opens its pipe a moment after starting.
        var until = DateTime.UtcNow + StartWait;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(300, ct);
            if (await TrySendAsync(request, ct) is { } started)
            {
                return started;
            }
        }

        return null;
    }

    private async Task<AutoTdpStatus?> TrySendAsync(AutoTdpRequest request, CancellationToken ct)
    {
        try
        {
            // Identification only: the service may see who is asking, never act as them.
            using var pipe = new NamedPipeClientStream(".", AutoTdpProtocol.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync((int)ConnectWait.TotalMilliseconds, ct);

            // Any program could open a pipe of that name while the service is not holding it, so the answer only
            // counts from the service: a program named so in session 0, where only services run.
            if (!IsService(pipe))
            {
                log.Warning($"TDP Engine {request.Command} refused: the pipe does not belong to the TDP Engine service");
                return null;
            }

            using var reply = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reply.CancelAfter(ReplyWait);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(AutoTdpProtocol.Write(request).AsMemory(), reply.Token);
            return AutoTdpProtocol.Read<AutoTdpStatus>(await reader.ReadLineAsync(reply.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Warning($"TDP Engine {request.Command}: no answer within {ReplyWait.TotalSeconds:0} seconds");
            return null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsService(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverId))
        {
            return false;
        }

        try
        {
            using var server = Process.GetProcessById((int)serverId);
            return server.SessionId == 0 && string.Equals(server.ProcessName, ServiceExeName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <returns>Whether sc.exe was started; the service itself answers, or not, on the pipe.</returns>
    private bool StartService()
    {
        try
        {
            using var sc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"),
                $"start {AutoTdpProtocol.ServiceName}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            sc?.WaitForExit(3000);
            return sc is not null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Warning($"Could not start TDP Engine: {ex.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
