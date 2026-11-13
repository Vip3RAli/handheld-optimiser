using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using HandheldOptimiser.PowerService;
using Fields = HandheldOptimiser.PowerService.AutoTdpProtocol.Music.Fields;
using Music = HandheldOptimiser.PowerService.AutoTdpProtocol.Music;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Runs the YouTube Music player: the launcher started by TDP Engine with --music when the widget asks for
/// it. One at a time. It holds the off screen player (<see cref="MusicPlayerWindow"/>) and answers the
/// widget over a pipe of its own, one line of JSON each way, until the widget closes it.
/// </summary>
internal static partial class MusicApp
{
    // The longest request it reads. A real one is well under a few hundred bytes.
    private const int MostRequest = 4096;

    // The longest search it passes on.
    private const int MostQuery = 200;

    // The longest it spends on one request before answering that it is busy.
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(8);

    public static void Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, Music.MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Program.Log($"Unhandled error in the music player: {e.Exception}");
            e.Handled = true;
        };

        var window = new MusicPlayerWindow();
        using var stop = new CancellationTokenSource();
        app.Startup += async (_, _) =>
        {
            // Shown off screen: the browser needs a window to draw in, even one nobody sees.
            window.Show();
            try
            {
                await window.StartAsync();
                Program.Log("Music player started");
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
            {
                Program.Log($"Music player could not start its browser: {ex.Message}");
                app.Shutdown();
                return;
            }

            _ = ServeAsync(window, app, stop.Token);
        };

        app.Run();
        stop.Cancel();
    }

    private static async Task ServeAsync(MusicPlayerWindow window, Application app, CancellationToken cancel)
    {
        var first = true;
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
                first = false;
            }
            catch (IOException ex)
            {
                Program.Log($"Music player could not open its pipe: {ex.Message}");
                app.Shutdown();
                return;
            }

            await using (pipe)
            {
                var quit = false;
                try
                {
                    await pipe.WaitForConnectionAsync(cancel);
                    var line = await ReadLineAsync(pipe, cancel);

                    // Answered on the window's thread, which the browser belongs to. A page that stalls (as one
                    // did behind a prompt, 2026-11-14) is not waited on for ever: the pipe takes one request at
                    // a time, so it would hold up every press after it, Close Player included.
                    var answering = app.Dispatcher.InvokeAsync(() => AnswerAsync(window, line)).Task.Unwrap();
                    var (answer, quitting) = await Task.WhenAny(answering, Task.Delay(AnswerWithin, cancel)) == answering
                        ? await answering
                        : (Write(MusicState.Nothing with { SignedIn = true }, null, "YouTube Music is busy. Try again in a moment."), false);
                    quit = quitting;
                    var bytes = Encoding.UTF8.GetBytes(answer + "\n");
                    await pipe.WriteAsync(bytes, cancel);
                    await pipe.FlushAsync(cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // The widget went away before its answer. The next one is waited for.
                }

                if (quit)
                {
                    Program.Log("Music player closed from the widget");
                    window.Quit();
                    app.Shutdown();
                    return;
                }
            }
        }
    }

    /// <summary>Answers one request: the player's state, with items for a search or the playlists.</summary>
    private static async Task<(string Answer, bool Quit)> AnswerAsync(MusicPlayerWindow window, string? line)
    {
        JsonObject? request = null;
        try
        {
            request = line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            // Answered as a request for the state.
        }

        var command = Text(request, Fields.Command) ?? Music.State;
        if (command == Music.Quit)
        {
            return (Write(MusicState.Nothing, null, null), true);
        }

        if (!window.Ready)
        {
            return (Write(MusicState.Nothing, null, "YouTube Music is starting"), false);
        }

        IReadOnlyList<MusicItem>? items = null;
        string? error = null;
        try
        {
            switch (command)
            {
                case Music.Toggle:
                    await window.ToggleAsync();
                    break;
                case Music.Next:
                    await window.SkipAsync(forward: true);
                    break;
                case Music.Previous:
                    await window.SkipAsync(forward: false);
                    break;
                case Music.Play:
                    if (!window.Play(Text(request, Fields.Kind), Text(request, Fields.Id)))
                    {
                        error = "That cannot be played";
                    }

                    break;
                case Music.Search:
                    var query = Text(request, Fields.Query)?.Trim();
                    items = string.IsNullOrEmpty(query) ? [] : await window.SearchAsync(query.Length > MostQuery ? query[..MostQuery] : query);
                    break;
                case Music.Playlists:
                    items = await window.PlaylistsAsync();
                    break;
                case Music.SignIn:
                    window.ShowSignIn();
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or COMException or InvalidOperationException)
        {
            Program.Log($"Music player could not {command}: {ex.Message}");
            error = "YouTube Music did not answer";
        }

        // A song chosen a moment ago is still loading, so the state is read as it now is and the widget asks again.
        return (Write(await window.StateAsync(), items, error), false);
    }

    private static string Write(MusicState state, IReadOnlyList<MusicItem>? items, string? error)
    {
        var answer = new JsonObject
        {
            [Fields.SignedIn] = state.SignedIn,
            [Fields.Playing] = state.Playing,
            [Fields.Title] = state.Title,
            [Fields.Artist] = state.Artist,
            [Fields.Art] = state.Art,
            [Fields.Position] = Math.Round(state.Position, 1),
            [Fields.Duration] = Math.Round(state.Duration, 1)
        };

        if (items is not null)
        {
            var list = new JsonArray();
            foreach (var item in items)
            {
                list.Add(new JsonObject
                {
                    [Fields.Kind] = item.Kind,
                    [Fields.Id] = item.Id,
                    [Fields.Title] = item.Title,
                    [Fields.Subtitle] = item.Subtitle
                });
            }

            answer[Fields.Items] = list;
        }

        if (error is not null)
        {
            answer[Fields.Error] = error;
        }

        return answer.ToJsonString();
    }

    private static string? Text(JsonObject? json, string name) =>
        json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>One line, read no further than the limit. Null when it runs past it or nothing came.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MostRequest];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0)
            {
                return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r');
            }
        }

        return null;
    }

    /// <summary>
    /// The player and the system may use the pipe, and the Game Bar widget, which runs in an app sandbox at
    /// low integrity, may talk to it: that one app package, labelled so a low-integrity client may write, as
    /// the TDP Engine service's pipe is. Only the first instance may create it, so no other program can sit
    /// on its name while the player runs.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(bool first)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Who is signed in could not be read");
        var sddl = $"D:(A;;GA;;;SY)(A;;GA;;;{user})";
        if (WidgetSid() is { } widget)
        {
            sddl += $"(A;;GRGW;;;{widget})S:(ML;;NW;;;LW)";
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, 0))
        {
            throw new IOException("The pipe's permissions could not be made");
        }

        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + Music.PipeName,
                PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0),
                PipeTypeByte | PipeRejectRemoteClients, 1, 0, 0, 0, ref attributes);
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
}
