using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Signs in to a store inside the library, over everything else, and keeps what the store hands back in
/// <see cref="StoreSessions"/>. Steam (stage 1 of the store sign-in work) is a QR code approved in the Steam
/// app on a phone, with Steam's own sign-in page as the fallback. GOG (stage 2) and Epic (stage 3) are
/// the store's own page. Xbox (stage 4) is Microsoft's page, which the account Windows is signed in with
/// goes straight through without showing.
///
/// The page is a WebView2 inside the library's own window, made when the page is asked for and disposed as
/// the sign-in closes. A second window would take the library out of the front, which stops the controller
/// and changes how the Old School layout draws. While the page is up the controller is passed to it as keys:
/// the D-pad as Tab and Shift+Tab, A as Enter, Y for Windows' touch keyboard.
/// </summary>
internal sealed class SignInPresenter
{
    private readonly SignInView _view;
    private readonly LibraryNavigation _navigation;
    private readonly NoticePresenter _notices;
    private readonly Func<nint> _windowHandle;
    private readonly Action _restoreFocus;
    private readonly Action<StoreSession> _signedIn;

    private CancellationTokenSource? _run;
    private WebView2? _web;
    private bool _onWebPage;

    // The store being signed in to. Only Steam has a QR code to switch to.
    private GameStore _store = GameStore.Steam;

    /// <summary>The store's cookies, so its page remembers the device, kept apart from the music player's.</summary>
    internal static string WebDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandheldOptimiser", "webview");

    /// <param name="signedIn">Told about a new sign-in once it is saved, so the library can list the account's games.</param>
    public SignInPresenter(SignInView view, LibraryNavigation navigation, NoticePresenter notices, Func<nint> windowHandle,
        Action restoreFocus, Action<StoreSession> signedIn)
    {
        _view = view;
        _navigation = navigation;
        _notices = notices;
        _windowHandle = windowHandle;
        _restoreFocus = restoreFocus;
        _signedIn = signedIn;

        view.CancelButton.Click += (_, _) => Cancel();
        view.SwitchButton.Click += (_, _) => Switch();
        view.KeyboardButton.Click += (_, _) => TouchKeyboard.Toggle();
    }

    public bool IsOpen => _navigation.SigningIn;

    /// <summary>Opens Steam's sign-in at its QR code. The menu, if open, has to be closed first.</summary>
    public void OpenSteam()
    {
        if (!_navigation.BeginSignIn())
        {
            return;
        }

        _store = GameStore.Steam;
        _view.SwitchButton.Visibility = Visibility.Visible;
        _view.Heading.Text = "Sign in to Steam";
        _view.QrHeading.Text = "Scan this with the Steam app on your phone";
        _view.QrSteps.Text = "In the Steam app, open Steam Guard and tap the QR code button, then point your phone at this code and approve.";
        _view.QrNote.Text = "Signing in here lists your Steam games and syncs their achievements. Steam itself still needs to be signed in to install and play them.";
        _view.Visibility = Visibility.Visible;
        Program.Log("Steam sign-in opened");
        ShowQr();
    }

    /// <summary>
    /// Opens GOG's own sign-in page. GOG has no QR code, so the page is all there is; it goes to a page with
    /// a code on it once signed in, which is swapped for the account's tokens.
    /// </summary>
    public void OpenGog()
    {
        if (!_navigation.BeginSignIn())
        {
            return;
        }

        _store = GameStore.Gog;
        _view.SwitchButton.Visibility = Visibility.Collapsed;
        _view.Heading.Text = "Sign in to GOG";
        _view.Visibility = Visibility.Visible;
        Program.Log("GOG sign-in opened");
        ShowWebPage();
    }

    /// <summary>
    /// Opens Epic's own sign-in page. Once signed in it goes to a page that is a line of JSON with a code in
    /// it, which is swapped for the account's tokens.
    /// </summary>
    public void OpenEpic()
    {
        if (!_navigation.BeginSignIn())
        {
            return;
        }

        _store = GameStore.Epic;
        _view.SwitchButton.Visibility = Visibility.Collapsed;
        _view.Heading.Text = "Sign in to Epic Games";
        _view.Visibility = Visibility.Visible;
        Program.Log("Epic sign-in opened");
        ShowWebPage();
    }

    /// <summary>
    /// Signs in to Xbox on Microsoft's own page. With the account Windows is signed in with, the page comes
    /// straight back with a code and is never seen; otherwise it asks as usual.
    /// </summary>
    public void OpenXbox()
    {
        if (!_navigation.BeginSignIn())
        {
            return;
        }

        _store = GameStore.Xbox;
        _view.SwitchButton.Visibility = Visibility.Collapsed;
        _view.Heading.Text = "Sign in to Xbox";
        _view.Visibility = Visibility.Visible;
        Program.Log("Xbox sign-in opened");
        ShowWebPage();
    }

    /// <summary>The QR code, or the web page: X, or the button for a finger.</summary>
    public void Switch()
    {
        if (!IsOpen || _store != GameStore.Steam)
        {
            return;
        }

        if (_onWebPage)
        {
            ShowQr();
        }
        else
        {
            ShowWebPage();
        }
    }

    /// <summary>Closes the sign-in without signing in: B, Escape, or the Cancel button.</summary>
    public void Cancel()
    {
        if (!IsOpen)
        {
            return;
        }

        Program.Log("Sign-in cancelled");
        Close();
    }

    /// <summary>A press of the controller while the sign-in is open.</summary>
    public void Press(GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.Back:
                Cancel();
                break;
            case GamepadAction.Options:
                Switch();
                break;
            case GamepadAction.QuickSettings when _onWebPage:
                TouchKeyboard.Toggle();
                break;
            case GamepadAction.Accept when _onWebPage:
                FocusPage();
                Native.PressKey(0x0D);
                break;
        }
    }

    /// <summary>The D-pad while the sign-in is open: through the web page's fields and buttons.</summary>
    public void Navigate(FocusNavigationDirection direction)
    {
        if (!_onWebPage)
        {
            return;
        }

        FocusPage();
        switch (direction)
        {
            case FocusNavigationDirection.Up:
                Native.PressKey(0x09, shift: true);
                break;
            case FocusNavigationDirection.Down:
                Native.PressKey(0x09);
                break;
            case FocusNavigationDirection.Left:
                Native.PressKey(0x25);
                break;
            case FocusNavigationDirection.Right:
                Native.PressKey(0x27);
                break;
        }
    }

    /// <summary>A key on the library's window while the sign-in is open. The web page takes its own keys.</summary>
    public void KeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void FocusPage()
    {
        if (_web is { } web && !web.IsKeyboardFocusWithin)
        {
            web.Focus();
        }
    }

    // ----- The QR code -----

    private void ShowQr()
    {
        var run = Restart();
        CloseWebPage();
        _view.Panel.Width = 940;
        _view.Panel.HorizontalAlignment = HorizontalAlignment.Center;
        _view.Panel.VerticalAlignment = VerticalAlignment.Center;
        _view.QrPart.Visibility = Visibility.Visible;
        _view.WebPart.Visibility = Visibility.Collapsed;
        _view.KeyboardButton.Visibility = Visibility.Collapsed;
        _view.SwitchText.Text = "Use the web page instead";
        _view.QrImage.Source = null;
        _view.QrWaiting.Text = "Asking Steam for a code...";
        _view.QrWaiting.Visibility = Visibility.Visible;
        _view.Status.Text = string.Empty;
        _ = RunQrAsync(run);
    }

    private async Task RunQrAsync(CancellationToken cancel)
    {
        // Steam drops a code that is not approved after a few minutes; a new one is asked for, a few times.
        for (var attempt = 0; attempt < 4 && !cancel.IsCancellationRequested; attempt++)
        {
            var challenge = await SteamAuth.BeginQrAsync(cancel);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            if (challenge is null)
            {
                _view.QrWaiting.Text = "No code";
                _view.Status.Text = "Steam could not be reached. Check the connection, or press X to use the web page.";
                return;
            }

            ShowCode(challenge.Url);
            _view.Status.Text = "Waiting for you to approve it on your phone...";

            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(challenge.Interval, cancel);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                var answer = await SteamAuth.PollAsync(challenge, cancel);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                switch (answer.State)
                {
                    case SteamAuth.PollState.NewCode:
                        challenge = challenge with { Url = answer.NewUrl!, ClientId = answer.NewClientId ?? challenge.ClientId };
                        ShowCode(challenge.Url);
                        continue;
                    case SteamAuth.PollState.Approved:
                        _view.Status.Text = "Approved. Signing in...";
                        await FinishSteamAsync(answer.RefreshToken!, answer.AccessToken, cancel);
                        return;
                    case SteamAuth.PollState.Ended:
                        break;
                    default:
                        continue;
                }

                break;
            }
        }

        if (!cancel.IsCancellationRequested)
        {
            _view.Status.Text = "The code ran out. Press X to use the web page, or B and try again.";
        }
    }

    private void ShowCode(string url)
    {
        var png = new QRCoder.PngByteQRCode(new QRCoder.QRCodeGenerator().CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        _view.QrImage.Source = image;
        _view.QrWaiting.Visibility = Visibility.Collapsed;
    }

    // ----- The web page -----

    private void ShowWebPage()
    {
        var run = Restart();
        _onWebPage = true;
        // As wide and tall as the screen allows, so the store's page is laid out as on a laptop.
        _view.Panel.Width = double.NaN;
        _view.Panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _view.Panel.VerticalAlignment = VerticalAlignment.Stretch;
        _view.QrPart.Visibility = Visibility.Collapsed;
        _view.WebPart.Visibility = Visibility.Visible;
        _view.KeyboardButton.Visibility = Visibility.Visible;
        _view.SwitchText.Text = "Use the QR code instead";
        _view.Status.Text = $"Opening {Game.NameOf(_store)}'s sign-in page...";
        _ = RunWebPageAsync(run);
    }

    /// <summary>The page in the sign-in, made fresh, with links that would open a window kept inside it.</summary>
    private async Task<CoreWebView2?> OpenPageAsync(CancellationToken cancel)
    {
        var web = new WebView2();
        _web = web;
        _view.WebPart.Child = web;
        var environment = await EnvironmentAsync();
        await web.EnsureCoreWebView2Async(environment);
        if (cancel.IsCancellationRequested || !ReferenceEquals(_web, web))
        {
            return null;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;

        // A link that would open a window of its own opens here instead, so nothing leaves the library.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        web.Focus();
        return core;
    }

    /// <summary>
    /// The browser the sign-in pages run in. It may use the account Windows itself is signed in with, which is
    /// what lets Microsoft's page sign in to Xbox without asking; no other store's page looks at it.
    /// </summary>
    private static Task<CoreWebView2Environment> EnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(null, WebDataFolder, new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true });

    private async Task RunXboxPageAsync(CancellationToken cancel)
    {
        if (await OpenPageAsync(cancel) is not { } core)
        {
            return;
        }

        // Microsoft's page goes to a blank page of its own with the code once signed in, or with why not.
        // That page is never loaded.
        var code = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && XboxAuth.IsRedirect(uri))
            {
                e.Cancel = true;
                code.TrySetResult(XboxAuth.CodeFrom(uri));
            }
        };

        core.Navigate(XboxAuth.SignInUrl);
        _view.Status.Text = "Signing in with your Microsoft account...";

        using (cancel.Register(() => code.TrySetCanceled()))
        {
            var got = await code.Task;
            if (got is null)
            {
                Program.Log("Xbox sign-in: Microsoft's page came back without a code");
                _view.Status.Text = "Microsoft did not finish the sign-in. Press B and try again.";
                return;
            }

            _view.Status.Text = "Signed in. Finishing...";
            await FinishXboxAsync(got, cancel);
        }
    }

    private async Task FinishXboxAsync(string code, CancellationToken cancel)
    {
        var (session, refused) = await XboxAccountApi.SignInAsync(code, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        if (session is null)
        {
            _view.Status.Text = refused
                ? "Xbox would not take this Microsoft account. It may have no Xbox profile yet: open the Xbox app once, then try again."
                : "Xbox could not be reached. Press B and try again.";
            return;
        }

        if (!StoreSessions.Save(session))
        {
            _view.Status.Text = "Signed in, but Windows would not keep the sign-in safely, so it was not saved.";
            return;
        }

        Program.Log("Signed in to Xbox inside the library");
        Close();
        _notices.Show(session.AccountName is { Length: > 0 } name
            ? $"Signed in to Xbox as {name}. Finding your games..."
            : "Signed in to Xbox. Finding your games...");
        _signedIn(session);
    }

    private async Task RunGogPageAsync(CancellationToken cancel)
    {
        if (await OpenPageAsync(cancel) is not { } core)
        {
            return;
        }

        // GOG's page goes to on_login_success with the code once signed in. That page is never loaded: the
        // code is all that is wanted from it.
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && GogAuth.CodeFrom(uri) is { } found)
            {
                e.Cancel = true;
                code.TrySetResult(found);
            }
        };

        core.Navigate(GogAuth.SignInUrl);
        _view.Status.Text = "Sign in as you would on GOG's site.";

        using (cancel.Register(() => code.TrySetCanceled()))
        {
            var got = await code.Task;
            _view.Status.Text = "Signed in. Finishing...";
            await FinishGogAsync(got, cancel);
        }
    }

    private async Task RunEpicPageAsync(CancellationToken cancel)
    {
        if (await OpenPageAsync(cancel) is not { } core)
        {
            return;
        }

        // Epic's page goes to a page of JSON with the code in it once signed in. An account its cookies
        // already know is sometimes sent to the store or its account page instead, and the page with the
        // code is asked for from there.
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var askedAgain = 0;
        core.NavigationCompleted += async (_, _) =>
        {
            try
            {
                if (code.Task.IsCompleted || !Uri.TryCreate(core.Source, UriKind.Absolute, out var uri))
                {
                    return;
                }

                if (EpicAuth.IsRedirectPage(uri))
                {
                    // The script's answer is the page's text as a JSON string.
                    var text = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.body.innerText"));
                    if (EpicAuth.ParseCode(text) is { } found)
                    {
                        // The code is on show in the page; it is put away before anything else.
                        core.Navigate("about:blank");
                        code.TrySetResult(found);
                    }
                    else if (askedAgain++ < 2)
                    {
                        // Not signed in after all: back to the sign-in page.
                        core.Navigate(EpicAuth.SignInUrl);
                    }
                }
                else if (EpicAuth.IsPastSignIn(uri) && askedAgain++ < 2)
                {
                    core.Navigate(EpicAuth.RedirectUrl);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException or ObjectDisposedException)
            {
                // The page was closed as it was being read.
            }
        };

        core.Navigate(EpicAuth.SignInUrl);
        _view.Status.Text = "Sign in as you would on Epic's site.";

        using (cancel.Register(() => code.TrySetCanceled()))
        {
            var got = await code.Task;
            _view.Status.Text = "Signed in. Finishing...";
            await FinishEpicAsync(got, cancel);
        }
    }

    private async Task RunWebPageAsync(CancellationToken cancel)
    {
        try
        {
            if (_store == GameStore.Gog)
            {
                await RunGogPageAsync(cancel);
                return;
            }

            if (_store == GameStore.Epic)
            {
                await RunEpicPageAsync(cancel);
                return;
            }

            if (_store == GameStore.Xbox)
            {
                await RunXboxPageAsync(cancel);
                return;
            }

            if (await OpenPageAsync(cancel) is not { } core)
            {
                return;
            }

            core.Navigate("https://store.steampowered.com/login/");
            _view.Status.Text = "Sign in as you would on Steam's site. Steam Guard asks as usual.";

            // Signed in once the store's sign-in cookie is set. Steam Guard and captchas are the page's business.
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(1500, cancel);
                var login = (await core.CookieManager.GetCookiesAsync("https://store.steampowered.com"))
                    .FirstOrDefault(c => c.Name == "steamLoginSecure")?.Value;
                if (login is null || SteamAuth.SplitLoginCookie(login) is not { } signedIn)
                {
                    continue;
                }

                // The refresh cookie keeps the sign-in alive without the page; Steam sets it as it signs in.
                var refresh = (await core.CookieManager.GetCookiesAsync("https://login.steampowered.com"))
                    .FirstOrDefault(c => c.Name == "steamRefresh_steam")?.Value is { } value && SteamAuth.SplitLoginCookie(value) is { } r
                    ? r.Token
                    : null;
                Program.Log(refresh is null
                    ? "Steam web sign-in: no refresh cookie, so it lasts a day"
                    : "Steam web sign-in: refresh cookie found");
                _view.Status.Text = "Signed in. Finishing...";
                await FinishSteamAsync(refresh, signedIn.Token, cancel, login);
                return;
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Program.Log($"{Game.NameOf(_store)}'s sign-in page could not open: {ex.GetType().Name}: {ex.Message}");
            if (!cancel.IsCancellationRequested)
            {
                _view.Status.Text = _store == GameStore.Steam
                    ? "The sign-in page could not open here. Press X to use the QR code."
                    : "The sign-in page could not open here.";
            }
        }
    }

    private void CloseWebPage()
    {
        _onWebPage = false;
        if (_web is { } web)
        {
            _web = null;
            _view.WebPart.Child = null;
            web.Dispose();
        }

        if (TouchKeyboard.IsShowing())
        {
            TouchKeyboard.Hide(_windowHandle());
        }
    }

    /// <summary>The store's sites, whose cookies a sign-out takes out of the page's profile.</summary>
    private static readonly Dictionary<GameStore, string[]> Sites = new()
    {
        [GameStore.Steam] =
        [
            "https://store.steampowered.com", "https://login.steampowered.com", "https://steamcommunity.com",
            "https://help.steampowered.com", "https://checkout.steampowered.com", "https://steam.tv",
        ],
        [GameStore.Gog] =
        [
            "https://auth.gog.com", "https://login.gog.com", "https://www.gog.com", "https://embed.gog.com", "https://gog.com",
        ],
        [GameStore.Xbox] =
        [
            "https://login.live.com", "https://account.live.com", "https://login.microsoftonline.com", "https://www.xbox.com",
        ],
        [GameStore.Epic] =
        [
            "https://www.epicgames.com", "https://epicgames.com", "https://store.epicgames.com", "https://accounts.epicgames.com",
            "https://launcher.store.epicgames.com",
        ],
    };

    /// <summary>
    /// Takes a store's cookies out of the sign-in page's profile, so the next sign-in starts from nothing.
    /// Only when the page has ever been opened; a browser is made for the moment, never shown.
    /// </summary>
    public async Task ForgetCookiesAsync(GameStore store)
    {
        if (!Directory.Exists(WebDataFolder) || !Sites.TryGetValue(store, out var sites) || _web is not null)
        {
            return;
        }

        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await EnvironmentAsync();
            controller = await environment.CreateCoreWebView2ControllerAsync(_windowHandle());
            controller.IsVisible = false;
            var cookies = controller.CoreWebView2.CookieManager;
            var removed = 0;
            foreach (var site in sites)
            {
                foreach (var cookie in await cookies.GetCookiesAsync(site))
                {
                    cookies.DeleteCookie(cookie);
                    removed++;
                }
            }

            Program.Log($"Took {removed} {Game.NameOf(store)} cookies out of the sign-in page's profile");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Program.Log($"Could not take the {Game.NameOf(store)} cookies out: {ex.GetType().Name}");
        }
        finally
        {
            controller?.Close();
        }
    }

    // ----- Finishing -----

    private async Task FinishSteamAsync(string? refreshToken, string? accessToken, CancellationToken cancel, string? cookie = null)
    {
        var steamId = SteamIdOf(refreshToken) ?? SteamIdOf(accessToken);
        if (steamId is null)
        {
            _view.Status.Text = "Steam's answer was not one the library knows. Try again, or use the other way in.";
            Program.Log("Steam sign-in: no SteamID in the tokens");
            return;
        }

        // The name Steam shows for the account, for the Store Accounts row.
        var name = await SteamAccountApi.PersonaNameAsync(steamId, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        var session = new StoreSession
        {
            Store = GameStore.Steam,
            AccountId = steamId,
            AccountName = name,
            RefreshToken = refreshToken,
            AccessToken = accessToken,
            AccessExpires = StoreSessions.JwtExpires(accessToken),
            Cookie = cookie,
        };

        if (!StoreSessions.Save(session))
        {
            _view.Status.Text = "Signed in, but Windows would not keep the sign-in safely, so it was not saved.";
            return;
        }

        Program.Log("Signed in to Steam inside the library");
        Close();
        _notices.Show(name is { Length: > 0 }
            ? $"Signed in to Steam as {name}. Finding your games..."
            : "Signed in to Steam. Finding your games...");
        _signedIn(session);
    }

    private async Task FinishGogAsync(string code, CancellationToken cancel)
    {
        var (tokens, _) = await GogAuth.ExchangeAsync(code, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        if (tokens is null)
        {
            _view.Status.Text = "GOG did not finish the sign-in. Press B and try again.";
            return;
        }

        var name = await GogAccountApi.UserNameAsync(tokens.AccessToken, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        var session = new StoreSession
        {
            Store = GameStore.Gog,
            AccountId = tokens.UserId,
            AccountName = name,
            RefreshToken = tokens.RefreshToken,
            AccessToken = tokens.AccessToken,
            AccessExpires = tokens.Expires,
        };

        if (!StoreSessions.Save(session))
        {
            _view.Status.Text = "Signed in, but Windows would not keep the sign-in safely, so it was not saved.";
            return;
        }

        Program.Log("Signed in to GOG inside the library");
        Close();
        _notices.Show(name is { Length: > 0 }
            ? $"Signed in to GOG as {name}. Finding your games..."
            : "Signed in to GOG. Finding your games...");
        _signedIn(session);
    }

    private async Task FinishEpicAsync(string code, CancellationToken cancel)
    {
        var (tokens, _) = await EpicAuth.ExchangeAsync(code, cancel);
        if (cancel.IsCancellationRequested)
        {
            return;
        }

        if (tokens is null)
        {
            _view.Status.Text = "Epic did not finish the sign-in. Press B and try again.";
            return;
        }

        var session = new StoreSession
        {
            Store = GameStore.Epic,
            AccountId = tokens.AccountId,
            AccountName = tokens.DisplayName,
            RefreshToken = tokens.RefreshToken,
            AccessToken = tokens.AccessToken,
            AccessExpires = tokens.Expires,
        };

        if (!StoreSessions.Save(session))
        {
            _view.Status.Text = "Signed in, but Windows would not keep the sign-in safely, so it was not saved.";
            return;
        }

        Program.Log("Signed in to Epic inside the library");
        Close();

        // An account with hundreds of games is asked about one game at a time the first time, which takes a while.
        _notices.Show(tokens.DisplayName is { Length: > 0 } name
            ? $"Signed in to Epic as {name}. Finding your games, which takes a minute the first time..."
            : "Signed in to Epic. Finding your games, which takes a minute the first time...");
        _signedIn(session);
    }

    /// <summary>The SteamID64 a Steam token was made for: its <c>sub</c> claim.</summary>
    internal static string? SteamIdOf(string? jwt) =>
        StoreSessions.JwtClaims(jwt) is { } claims && claims.TryGetProperty("sub", out var sub)
            && sub.GetString() is { } id && ulong.TryParse(id, out _)
            ? id
            : null;

    private CancellationToken Restart()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = new CancellationTokenSource();
        return _run.Token;
    }

    private void Close()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        CloseWebPage();
        _view.QrImage.Source = null;
        _view.Visibility = Visibility.Collapsed;
        _navigation.EndSignIn();
        _restoreFocus();
    }
}
