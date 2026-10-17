using System.IO;
using System.Net;
using System.Net.Http;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The library's one connection to the web, shared by everything that asks a service for something:
/// artwork, store details, deals, owned games and Epic's free games. Each of those used to have a client
/// of its own, each with its own pool of connections kept open behind games, and none of them said what
/// it was or how much it would read.
/// </summary>
internal static class HttpClients
{
    // Long enough for a slow store API on hotel Wi-Fi, short enough that a dead one does not hang a card.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    // The biggest answer read into memory: an owned games list of a few thousand games is about 1 MB.
    // Pictures are read with ReadCappedAsync, which has its own limit.
    private const long DefaultMaxResponseBytes = 16 * 1024 * 1024;

    // Wikipedia asks every program to say what it is, and GitHub's API refuses requests that do not.
    public static readonly string UserAgent =
        $"HandheldOptimiser/{typeof(HttpClients).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} (+https://github.com/Vip3RAli/handheld-optimiser)";

    /// <summary>The client for the library's ordinary requests.</summary>
    public static readonly HttpClient Shared = Create();

    /// <summary>A client with the library's user agent, for one that needs another timeout or limit.</summary>
    public static HttpClient Create(TimeSpan? timeout = null, long maxResponseBytes = DefaultMaxResponseBytes)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,

            // The library runs for days: connections are made again now and then, so a changed address
            // is picked up, and idle ones are closed soon rather than held open behind a game.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        };

        var http = new HttpClient(handler)
        {
            Timeout = timeout ?? DefaultTimeout,
            MaxResponseContentBufferSize = maxResponseBytes
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>
    /// The body of a response read with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, or null
    /// when it is bigger than <paramref name="maxBytes"/>. Read in pieces and given up on as soon as it
    /// passes the limit, so a server that sends no length, or the wrong one, cannot fill memory.
    /// </summary>
    public static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken cancel = default)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream(content.Headers.ContentLength is { } length ? (int)length : 0);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
