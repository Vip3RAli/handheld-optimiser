using System;
using System.Threading.Tasks;
using HandheldOptimiser.PowerService;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Imaging;

namespace HandheldOptimiser.GameBar
{
    /// <summary>
    /// The Music card: YouTube Music, played by the launcher's off screen player (its --music mode), which
    /// TDP Engine starts when asked. The card asks the player what is playing each second while it is open
    /// and the widget can be seen, and sends it the presses: play and pause, next, previous, a search, the
    /// player's playlists, and a song or playlist to play.
    /// </summary>
    public sealed partial class TdpWidget
    {
        // Glyphs from Segoe MDL2 Assets.
        private const string PlayGlyph = "";
        private const string PauseGlyph = "";

        // How long a start waits for the player to answer: the browser takes a few seconds the first time.
        private const int StartTries = 40;

        private readonly DispatcherTimer _musicTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private MusicStatus _music = MusicStatus.Off;
        private string _musicArt = "";
        private bool _musicBusy;
        private bool _musicTimerHooked;

        // Whether the list under the card is the player's playlists, which the My Playlists button folds away.
        private bool _showingPlaylists;

        /// <summary>Asks the player for news only while the card is open and the widget can be seen.</summary>
        private void UpdateMusicPolling()
        {
            if (!_musicTimerHooked)
            {
                _musicTimerHooked = true;
                _musicTimer.Tick += (sender, e) => RefreshMusic();
            }

            if (_loaded && (_widget == null || _widget.Visible) && MusicBody.Visibility == Visibility.Visible)
            {
                if (!_musicTimer.IsEnabled)
                {
                    RefreshMusic();
                    _musicTimer.Start();
                }
            }
            else
            {
                _musicTimer.Stop();
            }
        }

        private async void RefreshMusic()
        {
            if (_musicBusy)
            {
                return;
            }

            var status = await Task.Run(() => MusicClient.Ask(AutoTdpProtocol.Music.State));
            if (!_musicBusy)
            {
                // A list asked for stays until another is.
                status.Items = status.Items ?? _music.Items;
                ShowMusic(status);
            }
        }

        private async void OnMusicStart(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            if (_musicBusy)
            {
                return;
            }

            _musicBusy = true;
            MusicNote.Text = "Starting YouTube Music…";
            try
            {
                var status = await Task.Run(() =>
                {
                    var engine = EngineClient.StartMusic();
                    if (engine.Error != null && !engine.Reachable)
                    {
                        return new MusicStatus { Error = engine.Error };
                    }

                    // The player opens its pipe once its browser is up.
                    for (var tries = 0; tries < StartTries; tries++)
                    {
                        var answer = MusicClient.Ask(AutoTdpProtocol.Music.State);
                        if (answer.Running && answer.Error == null)
                        {
                            return answer;
                        }

                        System.Threading.Thread.Sleep(500);
                    }

                    return new MusicStatus { Error = "YouTube Music did not start. Try again in a moment." };
                });
                ShowMusic(status);
            }
            finally
            {
                _musicBusy = false;
            }
        }

        private void OnMusicSignIn(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            MusicNote.Text = "Sign in in the YouTube Music window, then come back here. Close Game Bar to see it.";
            SendMusic(AutoTdpProtocol.Music.SignIn);
        }

        private void OnMusicToggle(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            SendMusic(AutoTdpProtocol.Music.Toggle);
        }

        private void OnMusicNext(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            SendMusic(AutoTdpProtocol.Music.Next);
        }

        private void OnMusicPrevious(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            SendMusic(AutoTdpProtocol.Music.Previous);
        }

        /// <summary>
        /// Opens the player's playlists, or folds them away when they are open: a long library made the widget
        /// very long, with no way to put it away (the player asked, 2026-11-14).
        /// </summary>
        private void OnMusicPlaylists(object sender, RoutedEventArgs e)
        {
            if (_showingPlaylists)
            {
                ClearMusicList();
                return;
            }

            MusicPlaylistsTile.IsChecked = false;
            MusicNote.Text = "Fetching your playlists…";
            MusicNote.Visibility = Visibility.Visible;
            SendMusic(AutoTdpProtocol.Music.Playlists, playlists: true);
        }

        private void ClearMusicList()
        {
            _showingPlaylists = false;
            MusicPlaylistsTile.IsChecked = false;
            _music.Items = null;
            ShowMusicItems(_music);
        }

        private void OnMusicSearch(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            Search();
        }

        private void OnMusicQueryKey(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Search();
            }
        }

        private void Search()
        {
            var query = MusicQuery.Text.Trim();
            if (query.Length == 0)
            {
                return;
            }

            MusicNote.Text = "Searching…";
            SendMusic(AutoTdpProtocol.Music.Search, query: query);
        }

        private async void OnMusicQuit(object sender, RoutedEventArgs e)
        {
            Unpress(sender);
            _musicBusy = true;
            try
            {
                await Task.Run(() => MusicClient.Ask(AutoTdpProtocol.Music.Quit));
                _showingPlaylists = false;
                MusicPlaylistsTile.IsChecked = false;
                ShowMusic(MusicStatus.Off);
            }
            finally
            {
                _musicBusy = false;
            }
        }

        private void OnMusicItem(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is MusicItem item)
            {
                SendMusic(AutoTdpProtocol.Music.Play, item.Kind, item.Id);
            }
        }

        /// <summary>Sends one press to the player on a background thread and shows its answer.</summary>
        /// <param name="playlists">Whether this asks for the playlists, so the list that comes back is theirs.</param>
        private async void SendMusic(string command, string kind = null, string id = null, string query = null, bool playlists = false)
        {
            if (_musicBusy)
            {
                return;
            }

            _musicBusy = true;
            try
            {
                var status = await Task.Run(() => MusicClient.Ask(command, kind, id, query));

                // A new list says whose it is: the playlists, or a search's, which the playlists button does not fold.
                if (status.Items != null)
                {
                    _showingPlaylists = playlists;
                }

                MusicPlaylistsTile.IsChecked = _showingPlaylists;
                status.Items = status.Items ?? _music.Items;
                ShowMusic(status);
            }
            finally
            {
                _musicBusy = false;
            }
        }

        private void ShowMusic(MusicStatus status)
        {
            var listChanged = !ReferenceEquals(status.Items, _music.Items);
            _music = status;

            MusicStart.Visibility = Shown(!status.Running);
            MusicSignIn.Visibility = Shown(status.Running && !status.SignedIn);
            MusicPlayer.Visibility = Shown(status.Running && status.SignedIn);

            MusicNote.Text = status.Error
                ?? (!status.Running ? "Plays YouTube Music in the background. Sign in once, then play and pick music from here."
                : !status.SignedIn ? "Sign in to your Google account once. The window opens on the desktop."
                : status.Items != null && status.Items.Count == 0 ? "Nothing found."
                : "");
            MusicNote.Visibility = Shown(MusicNote.Text.Length > 0);

            if (!status.Running || !status.SignedIn)
            {
                return;
            }

            var playing = status.Title.Length > 0;
            MusicTitle.Text = playing ? status.Title : "Nothing playing";
            MusicArtist.Text = playing ? status.Artist : "Pick a playlist or search";
            MusicToggle.Content = status.Playing ? PauseGlyph : PlayGlyph;
            MusicProgress.Value = status.Duration > 0 ? Math.Min(1, status.Position / status.Duration) : 0;

            if (status.Art != _musicArt)
            {
                _musicArt = status.Art;
                MusicArt.Source = Uri.TryCreate(status.Art, UriKind.Absolute, out var art) && art.Scheme == "https"
                    ? new BitmapImage(art) { DecodePixelWidth = 192 }
                    : null;
            }

            if (listChanged)
            {
                ShowMusicItems(status);
            }
        }

        private void ShowMusicItems(MusicStatus status)
        {
            MusicItems.Children.Clear();
            if (status.Items == null)
            {
                return;
            }

            foreach (var item in status.Items)
            {
                var text = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                text.Children.Add(new TextBlock { Text = item.Title, Style = (Style)Resources["Label"], TextTrimming = TextTrimming.CharacterEllipsis });
                if (item.Subtitle.Length > 0)
                {
                    text.Children.Add(new TextBlock { Text = item.Subtitle, Style = (Style)Resources["Note"], TextTrimming = TextTrimming.CharacterEllipsis });
                }

                var button = new Button { Style = (Style)Resources["Fold"], Tag = item, Content = text };
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Title);
                button.Click += OnMusicItem;
                MusicItems.Children.Add(button);
            }
        }

        // The tiles are toggle buttons; a press here is an action, so none stays lit.
        private static void Unpress(object sender)
        {
            if (sender is ToggleButton tile)
            {
                tile.IsChecked = false;
            }
        }
    }
}
