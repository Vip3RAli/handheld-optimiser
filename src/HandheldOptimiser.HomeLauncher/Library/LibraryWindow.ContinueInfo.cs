using System.IO;
using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One line of the panel beside the Continue playing card, such as "Time played" and "12 h 5 min".</summary>
internal sealed record InfoRow(string Label, string Value);

/// <summary>
/// The panel beside the Continue playing card: the game's play time, size on disk and profile, and what
/// its store says about it when the player switched Store details on.
/// </summary>
public partial class LibraryWindow
{
    // How much bigger the card and the panel are drawn than LibraryWindow.xaml lays them out: half as big
    // again above the grid, and as they are above the row. The widths below are before that.
    private double ContinueScale => IsRow ? 1 : 1.5;

    // The card in DIPs including its margins, as LibraryWindow.xaml lays it out.
    private const double ContinueCardWidth = 580;

    // The least room the rows need beside the card, and the least for the description beside the rows.
    private const double InfoMinWidth = 280;
    private const double AboutMinWidth = 640;

    // How wide the rows are, alone and with the description beside them.
    private const double InfoWidthAlone = 440;
    private const double RowsWidth = 300;

    private bool _continueInfo = LibrarySettings.ContinueInfo;
    private bool _storeDetails = LibrarySettings.StoreDetails;

    // The game the panel is about, and what was found for it, so the rows are rebuilt without asking again.
    private string? _infoKey;
    private long? _infoSize;
    private GameDetails? _infoDetails;

    /// <summary>Whether the panel has room beside the card on this screen.</summary>
    private bool ContinueInfoFits() => _continueInfo && InfoRoom() >= InfoMinWidth;

    private double InfoRoom() =>
        (Scroller.ActualWidth - Scroller.Padding.Left - Scroller.Padding.Right) / ContinueScale - ContinueCardWidth;

    /// <summary>Fills the panel for the game on the card, or hides it when it is off or does not fit.</summary>
    private void UpdateContinueInfo(GameTile card)
    {
        if (!ContinueInfoFits())
        {
            ContinueInfo.Visibility = Visibility.Collapsed;
            return;
        }

        if (card.Game.Key == _infoKey)
        {
            ShowContinueInfo(card);
            return;
        }

        _infoKey = card.Game.Key;
        _infoSize = null;
        _infoDetails = _storeDetails ? StoreDetails.Cached(card.Game) : null;
        ShowContinueInfo(card);
        LoadContinueInfo(card);
    }

    /// <summary>Measures the game's folder and asks for its store details, both of which take a while.</summary>
    private async void LoadContinueInfo(GameTile card)
    {
        var game = card.Game;
        var details = _storeDetails && _infoDetails is null ? Task.Run(() => StoreDetails.FindAsync(game)) : null;

        // A program the player added may sit in a folder full of other things.
        if (game.Store is not (GameStore.Other or GameStore.Emulator) && game.InstallDirectory is { } folder)
        {
            var size = await Task.Run(() => Directory.Exists(folder) ? Cleanup.SizeOf([folder]) : 0);

            // The card may have moved on to another game meanwhile.
            if (_infoKey != game.Key)
            {
                return;
            }

            _infoSize = size;
            ShowContinueInfo(card);
        }

        if (details is not null && await details is { } found && _infoKey == game.Key && _storeDetails)
        {
            _infoDetails = found;
            ShowContinueInfo(card);
        }
    }

    private void ShowContinueInfo(GameTile card)
    {
        var stats = _stats.GetValueOrDefault(card.Game.Key);
        var rows = new List<InfoRow>();
        if (stats.Played >= TimeSpan.FromMinutes(1))
        {
            rows.Add(new InfoRow("Time played", PlayStats.Duration(stats.Played)));
        }

        if (_infoSize is { } size && size > 0)
        {
            rows.Add(new InfoRow("Size on disk", CleanResult.Size(size)));
        }

        var profile = GameProfiles.For(card.Game);
        if (!profile.IsEmpty || profile.CloseApps is not null || profile.AutoTdpFps is not null)
        {
            rows.Add(new InfoRow("Game profile", Capitalised(profile.Describe())));
        }

        if (_infoDetails is { } details)
        {
            if (details.Genres.Length > 0)
            {
                rows.Add(new InfoRow("Genre", string.Join(", ", details.Genres.Take(3))));
            }

            if (details.Released is { } released)
            {
                rows.Add(new InfoRow("Released", released));
            }

            if (details.Developer is { } developer)
            {
                rows.Add(new InfoRow("Developer", developer));
            }
        }

        var about = InfoRoom() >= AboutMinWidth ? _infoDetails?.Description : null;
        if (rows.Count == 0 && about is null)
        {
            ContinueInfo.Visibility = Visibility.Collapsed;
            return;
        }

        ContinueRows.ItemsSource = rows;
        ContinueAbout.Text = about;
        ContinueAbout.Visibility = about is null ? Visibility.Collapsed : Visibility.Visible;

        var source = about is null ? null : _infoDetails?.Source;
        ContinueSource.Text = source is null ? null : $"From {source}";
        ContinueSource.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        // With no description the rows would be lost in a panel as wide as the screen.
        ContinueRows.Width = about is null ? double.NaN : RowsWidth;
        ContinueInfo.Width = about is null ? Math.Min(InfoWidthAlone, InfoRoom() - 20) : double.NaN;
        ContinueInfo.HorizontalAlignment = about is null ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ContinueInfo.Visibility = Visibility.Visible;
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void OnToggleContinueInfo(object sender, RoutedEventArgs e)
    {
        _continueInfo = !_continueInfo;
        LibrarySettings.ContinueInfo = _continueInfo;
        RefreshSettings();
        UpdateContinue();
    }

    private void OnToggleStoreDetails(object sender, RoutedEventArgs e)
    {
        _storeDetails = !_storeDetails;
        LibrarySettings.StoreDetails = _storeDetails;
        RefreshSettings();

        // Asked for again, or taken away, for the game on the card.
        _infoKey = null;
        UpdateContinue();
    }
}
