using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The downloads under way, where they can be seen without opening Ready to install: an arrow with how far
/// they have got in the status pill at the top right, and a small panel under the pill that a tap on it
/// opens, a row per game kept up to date as Steam goes. The controller cannot reach the pill, so it has the
/// Downloads list in Catalog &amp; Content, whose row shows only while there is something downloading. The
/// user asked for the pill on 2026-11-14, and then for the panel in place of the full menu.
/// </summary>
internal sealed class DownloadsPresenter
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly TopBarView _topBar;
    private readonly NoticePresenter _notices;

    private IReadOnlyList<DownloadRow> _rows = [];

    // Each source's latest rows: Steam's own installs, and the library's (GOG).
    private readonly Dictionary<string, IReadOnlyList<DownloadRow>> _bySource = [];

    /// <summary>The library's own installs, which a GOG row pauses and carries on.</summary>
    public InstallQueue? Queue { get; init; }

    private static bool IsLibraryDownload(DownloadRow row) =>
        row.AppId.StartsWith("gog:", StringComparison.Ordinal) || row.AppId.StartsWith("epic:", StringComparison.Ordinal);

    // The list's rows, by app id, so a new reading changes their text in place and the focus stays put.
    private readonly Dictionary<string, Button> _shown = [];

    // When the panel last closed. The tap on the arrow that closes it would otherwise open it again.
    private long _closedAt;

    public DownloadsPresenter(MenuView view, MenuPresenter menu, TopBarView topBar, NoticePresenter notices)
    {
        _view = view;
        _menu = menu;
        _topBar = topBar;
        _notices = notices;

        var popup = topBar.DownloadsPopup;
        popup.PlacementTarget = topBar.Hud;
        popup.CustomPopupPlacementCallback = UnderTheCapsule;
        // Closed is raised after the tap that closed the panel has reached the arrow, so the moment IsOpen
        // turns false is what is kept.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
            .AddValueChanged(popup, (_, _) =>
            {
                if (!popup.IsOpen)
                {
                    _closedAt = Environment.TickCount64;
                }
            });

        topBar.DownloadsButton.Click += (_, _) => TogglePanel();
        topBar.DownloadsPopupSteam.Click += (_, _) => OpenSteam();
        view.DownloadsGroup.Click += (_, _) => OpenList();
        view.DownloadsSteamItem.Click += (_, _) => OpenSteam();
        view.DownloadsBackItem.Click += (_, _) => menu.Back();
    }

    private bool Showing => ReferenceEquals(_menu.List, _view.DownloadsItems);

    /// <summary>A new look at one source's downloads: <see cref="SteamInstallWatcher"/>'s or <see cref="InstallQueue"/>'s.</summary>
    public void Update(string source, IReadOnlyList<DownloadRow> rows)
    {
        _bySource[source] = rows;
        _rows = rows = _bySource.Values.SelectMany(r => r).ToList();
        var any = rows.Count > 0;
        _topBar.DownloadsPopupSteam.Visibility = rows.Any(r => !IsLibraryDownload(r)) ? Visibility.Visible : Visibility.Collapsed;
        _view.DownloadsSteamItem.Visibility = _topBar.DownloadsPopupSteam.Visibility;
        _topBar.DownloadsButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _topBar.DownloadsText.Text = PillText(rows);
        _view.DownloadsGroup.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _view.DownloadsGroup.Tag = any ? PillText(rows) : null;

        // A new list each time: the panel takes no focus, so nothing is lost by building its rows again.
        _topBar.DownloadsList.ItemsSource = rows;
        if (!any)
        {
            _topBar.DownloadsPopup.IsOpen = false;
        }

        if (Showing)
        {
            Fill();
        }
    }

    /// <summary>
    /// What the pill says beside its arrow: the figure for the one download, or how many there are. Nothing
    /// when the one download's figure is not known, so the arrow alone says something is coming down.
    /// </summary>
    internal static string PillText(IReadOnlyList<DownloadRow> rows) => rows switch
    {
        [] => string.Empty,
        [{ Progress: var only }] => only.Phase switch
        {
            InstallPhase.Paused => "Paused",
            InstallPhase.Waiting => "Queued",
            _ when only.Total > 0 => $"{only.Percent}%",
            _ => string.Empty
        },
        _ => $"{rows.Count} games"
    };

    private void TogglePanel()
    {
        var popup = _topBar.DownloadsPopup;
        if (popup.IsOpen || Environment.TickCount64 - _closedAt < 300)
        {
            popup.IsOpen = false;
            return;
        }

        popup.IsOpen = _rows.Count > 0;
    }

    // The panel's right edge lines up with the capsule's, a little below it.
    private static CustomPopupPlacement[] UnderTheCapsule(Size popup, Size target, Point offset) =>
        [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 8), PopupPrimaryAxis.Horizontal)];

    /// <summary>The Downloads list in Catalog &amp; Content, for the controller.</summary>
    private void OpenList()
    {
        _menu.Show(_view.DownloadsItems, "Downloads", SettingsMenu.GamesApps);
        _menu.ClearRows(_view.DownloadsItems);
        _shown.Clear();
        Fill();
        _menu.ShowItems(_shown.Values.FirstOrDefault());
    }

    /// <summary>Adds a row for each new download, takes away finished ones, and updates the rest in place.</summary>
    private void Fill()
    {
        var lostFocus = false;
        foreach (var gone in _shown.Keys.Except(_rows.Select(r => r.AppId)).ToList())
        {
            if (_shown[gone].IsKeyboardFocused)
            {
                lostFocus = true;
            }

            _view.DownloadsItems.Children.Remove(_shown[gone]);
            _shown.Remove(gone);
        }

        foreach (var row in _rows)
        {
            if (!_shown.TryGetValue(row.AppId, out var button))
            {
                var key = row.AppId;
                var library = IsLibraryDownload(row);
                var store = row.AppId.StartsWith("epic:", StringComparison.Ordinal) ? "Epic Games" : "GOG";
                button = _menu.AddRow(_view.DownloadsItems, _view.DownloadsSteamItem, row.Title, null,
                    library ? () => Queue?.Toggle(key) : OpenSteam);
                MenuRow.SetAbout(button, library
                    ? $"Downloading here, from {store}. Press to pause or carry on. Cancel it from the game's own menu."
                    : "Open Steam's downloads to pause, resume or cancel it.");
                _shown[row.AppId] = button;
            }

            button.Tag = row.Progress.Text;
        }

        if (lostFocus && Showing)
        {
            _menu.ShowItems(_shown.Values.FirstOrDefault()
                ?? (_view.DownloadsSteamItem.IsVisible ? _view.DownloadsSteamItem : _view.DownloadsBackItem));
        }

        if (_rows.Count == 0 && Showing)
        {
            _notices.Show("Nothing is downloading now.");
        }
    }

    private void OpenSteam()
    {
        _topBar.DownloadsPopup.IsOpen = false;
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true })?.Dispose();
            if (Showing)
            {
                _menu.Close();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notices.Show("Steam could not be opened.");
            Program.Log($"Could not open Steam's downloads: {ex.Message}");
        }
    }
}
