using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using HandheldOptimiser.PowerService;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What the player is doing, as the widget shows it.</summary>
internal sealed record MusicState(bool SignedIn, bool Playing, string Title, string Artist, string Art, double Position, double Duration)
{
    public static readonly MusicState Nothing = new(false, false, "", "", "", 0, 0);
}

/// <summary>
/// The YouTube Music page in a browser (WebView2) that is kept off screen, signed in to the player's Google
/// account. It plays what the widget asks for, reports what is playing from the page's own media details,
/// and fetches playlists and search results from YouTube Music's web service with the page's sign-in. It
/// comes on screen only to sign in, and is closed only by <see cref="Quit"/>; the close button puts it back
/// off screen, still playing.
/// </summary>
internal sealed partial class MusicPlayerWindow : Window
{
    // Off screen, out of the taskbar and out of Alt+Tab, where it plays unseen.
    private const double Away = -32000;

    // Saved beside the library's own data, so the sign-in is kept between runs.
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "Music");

    /// <summary>
    /// Run as each page is made, before the page's own scripts: keeps the page's handlers for the media
    /// keys, so next and previous work as the page's own buttons do, wherever those are drawn.
    /// </summary>
    private const string KeepHandlers = """
        (() => {
          const session = navigator.mediaSession;
          if (!session || session.__handheldOptimiser) return;
          const set = session.setActionHandler.bind(session);
          window.__handheldOptimiserActions = {};
          session.setActionHandler = (action, handler) => { window.__handheldOptimiserActions[action] = handler; return set(action, handler); };
          session.__handheldOptimiser = true;
        })();
        """;

    private const string ReadState = """
        (() => {
          const video = document.querySelector('video');
          const data = navigator.mediaSession && navigator.mediaSession.metadata;
          const art = data && data.artwork && data.artwork.length ? data.artwork[data.artwork.length - 1].src : '';
          return {
            title: data ? data.title : '', artist: data ? data.artist : '', art,
            playing: !!video && !video.paused,
            position: video ? video.currentTime || 0 : 0,
            duration: video && isFinite(video.duration) ? video.duration : 0
          };
        })()
        """;

    private const string TogglePlay = """
        (() => {
          const video = document.querySelector('video');
          if (!video) return false;
          if (video.paused) video.play(); else video.pause();
          return true;
        })()
        """;

    private const string ReadContext = "JSON.stringify((window.ytcfg && ytcfg.get('INNERTUBE_CONTEXT')) || null)";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly WebView2 _browser = new();
    private bool _quitting;
    private bool _signingIn;

    public MusicPlayerWindow()
    {
        Title = "YouTube Music - Handheld Optimiser";
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _browser;
        PutAway();
    }

    /// <summary>Whether the page is ready to be asked anything.</summary>
    public bool Ready => _browser.CoreWebView2 is not null;

    /// <summary>Starts the browser on YouTube Music. The window must be shown first, as the browser needs one.</summary>
    public async Task StartAsync()
    {
        // Music has to start from a request with no click on the page, as the page is never clicked. The page
        // is never seen, so it is drawn without the graphics card, which is the game's, and in one renderer:
        // measured on the Ally (2026-11-14), the browser took 555 MB private without these.
        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required --disable-gpu --renderer-process-limit=1 --disable-background-networking");
        var environment = await CoreWebView2Environment.CreateAsync(null, DataFolder, options);
        await _browser.EnsureCoreWebView2Async(environment);

        var core = _browser.CoreWebView2!;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;

        // The page's own prompts are answered here and never shown. YouTube Music asks "Leave site?" when
        // anything new is played while a song is on, and that prompt, unlike the window, lands on the
        // desktop, where it held up the player until it was answered (2026-11-14). Leaving is always right:
        // playing something else is what the player asked for. Any other prompt is dismissed.
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.ScriptDialogOpening += (_, e) =>
        {
            if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
            {
                e.Accept();
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeepHandlers);

        // Back from signing in, the window goes away again.
        core.NavigationCompleted += async (_, _) =>
        {
            if (_signingIn && core.Source.StartsWith(YouTubeMusic.Origin, StringComparison.OrdinalIgnoreCase) && await SignedInAsync())
            {
                _signingIn = false;
                PutAway();
            }
        };

        // A link that would open a window of its own opens in this one.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                core.Navigate(e.Uri);
            }
        };

        core.Navigate(YouTubeMusic.Home);
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
    }

    /// <summary>Whether the page holds a Google sign-in.</summary>
    public async Task<bool> SignedInAsync() => await SapisidAsync() is not null;

    /// <summary>What is playing, from the page.</summary>
    public async Task<MusicState> StateAsync()
    {
        var signedIn = await SignedInAsync();
        try
        {
            using var document = JsonDocument.Parse(await _browser.CoreWebView2!.ExecuteScriptAsync(ReadState));
            var root = document.RootElement;
            return new MusicState(signedIn,
                root.GetProperty("playing").GetBoolean(),
                root.GetProperty("title").GetString() ?? "",
                root.GetProperty("artist").GetString() ?? "",
                root.GetProperty("art").GetString() ?? "",
                root.GetProperty("position").GetDouble(),
                root.GetProperty("duration").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or COMException)
        {
            return MusicState.Nothing with { SignedIn = signedIn };
        }
    }

    public Task ToggleAsync() => _browser.CoreWebView2!.ExecuteScriptAsync(TogglePlay);

    /// <summary>The next or previous song, through the page's own handler, or its button when it has none.</summary>
    public Task SkipAsync(bool forward)
    {
        var action = forward ? "nexttrack" : "previoustrack";
        var button = forward ? ".next-button" : ".previous-button";
        return _browser.CoreWebView2!.ExecuteScriptAsync($$"""
            (() => {
              const handler = window.__handheldOptimiserActions && window.__handheldOptimiserActions['{{action}}'];
              if (handler) { handler({ action: '{{action}}' }); return true; }
              const button = document.querySelector('{{button}}');
              if (button) { button.click(); return true; }
              return false;
            })()
            """);
    }

    /// <summary>Plays a song or playlist. False when the id is not one YouTube makes.</summary>
    public bool Play(string? kind, string? id)
    {
        if (YouTubeMusic.PlayUrl(kind, id) is not { } url)
        {
            return false;
        }

        _browser.CoreWebView2!.Navigate(url);
        return true;
    }

    /// <summary>The player's own playlists.</summary>
    public Task<IReadOnlyList<MusicItem>> PlaylistsAsync() => AskAsync("browse", YouTubeMusic.PlaylistsBody);

    /// <summary>Songs and playlists for a search.</summary>
    public Task<IReadOnlyList<MusicItem>> SearchAsync(string query) =>
        AskAsync("search", context => YouTubeMusic.SearchBody(context, query));

    /// <summary>Brings the window on screen at Google's sign-in. It goes away again once back at YouTube Music signed in.</summary>
    public void ShowSignIn()
    {
        _signingIn = true;
        var area = SystemParameters.WorkArea;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        ShowInTaskbar = true;
        SetToolWindow(false);
        Activate();
        _browser.CoreWebView2?.Navigate(YouTubeMusic.SignIn);
    }

    /// <summary>Closes the window and the browser for good.</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetToolWindow(true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closed by the player while signing in, it goes back off screen and keeps playing.
        if (!_quitting)
        {
            e.Cancel = true;
            _signingIn = false;
            PutAway();
        }

        base.OnClosing(e);
    }

    private void PutAway()
    {
        Left = Away;
        Top = Away;
        ShowInTaskbar = false;
        ShowActivated = false;
        SetToolWindow(true);
    }

    // Out of Alt+Tab while away, as a tool window.
    private void SetToolWindow(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        var style = GetWindowLongPtrW(handle, ExStyle);
        SetWindowLongPtrW(handle, ExStyle, on ? style | ExToolWindow : style & ~ExToolWindow);
    }

    private const int ExStyle = -20;
    private const nint ExToolWindow = 0x00000080;

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    private async Task<string?> SapisidAsync()
    {
        if (_browser.CoreWebView2 is not { } core)
        {
            return null;
        }

        var cookies = await core.CookieManager.GetCookiesAsync(YouTubeMusic.Origin);
        return cookies.FirstOrDefault(c => c.Name == "SAPISID")?.Value;
    }

    /// <summary>Asks YouTube Music's web service, signed in as the page is, and reads the items out of its answer.</summary>
    private async Task<IReadOnlyList<MusicItem>> AskAsync(string endpoint, Func<string?, string> body)
    {
        var core = _browser.CoreWebView2!;
        string? context = null;
        try
        {
            // The script's answer is JSON, and the context in it a string of JSON.
            context = JsonSerializer.Deserialize<string?>(await core.ExecuteScriptAsync(ReadContext));
        }
        catch (JsonException)
        {
            // The plain context is used.
        }

        var cookies = await core.CookieManager.GetCookiesAsync(YouTubeMusic.Origin);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{YouTubeMusic.Origin}/youtubei/v1/{endpoint}?prettyPrint=false")
        {
            Content = new StringContent(body(context), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        request.Headers.TryAddWithoutValidation("Origin", YouTubeMusic.Origin);
        request.Headers.TryAddWithoutValidation("X-Origin", YouTubeMusic.Origin);
        request.Headers.TryAddWithoutValidation("Referer", YouTubeMusic.Home);
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        request.Headers.TryAddWithoutValidation("User-Agent", core.Settings.UserAgent);
        if (cookies.FirstOrDefault(c => c.Name == "SAPISID")?.Value is { } sapisid)
        {
            request.Headers.TryAddWithoutValidation("Authorization", YouTubeMusic.AuthHeader(sapisid, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Program.Log($"YouTube Music answered {(int)response.StatusCode} to a {endpoint}");
            return [];
        }

        return YouTubeMusic.Items(await response.Content.ReadAsStringAsync());
    }
}
