using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Last played: the last game played as a wide card above the grid, so it is a press away whatever
/// order the grid is in.
/// </summary>
public partial class LibraryWindow
{
    // The card's artwork is decoded at about the card's width at 200% scaling.
    private const int ContinueArtWidth = 1680;

    // The space above the Game library heading in DIPs, and the most it grows to on a TV or monitor.
    private const double AllGamesGap = 36;
    private const double AllGamesMaxGap = 240;

    // A row of tiles with its titles, until the tiles have been laid out.
    private const double TileRowHeight = 317;

    private bool _continuePlaying =LibrarySettings.ContinuePlaying;

    // Whether the focus is in the Last played row rather than the grid, to put it back there.
    private bool _focusInContinue;

    // The game whose artwork is on the card, or on its way there.
    private string? _continueArtKey;

    /// <summary>Fills the Last played row, or hides it when it is off or does not apply.</summary>
    private void UpdateContinue()
    {
        // The Old School cards take the middle of the screen, and the first of them is the last game played.
        var played = _continuePlaying && _filter == TileFilter.All && !IsOldSchool
            ? RecentGames.Pick(ShownTiles().Where(t => t.IsInstalled && !t.IsHidden && !t.IsApp),
                t => _stats.GetValueOrDefault(t.Game.Key).LastPlayed, 1)
            : [];

        if (played.Count == 0)
        {
            ContinuePanel.Visibility = Visibility.Collapsed;
            _focusInContinue = false;
            return;
        }

        // Above the grid the card is bigger and in the middle; above the row it is at the left, at the
        // tiles' own size, and the row needs no heading of its own.
        var row = IsRow;
        ContinueZoom.ScaleX = ContinueZoom.ScaleY = ContinueScale;
        ContinueRow.HorizontalAlignment = row ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        AllGamesHeading.Visibility = row ? Visibility.Collapsed : Visibility.Visible;

        var card = played[0];
        ContinuePanel.Visibility = Visibility.Visible;
        ContinueCard.DataContext = card;
        UpdateContinueInfo(card);

        // The play time is in the panel beside the card when that is showing.
        ContinueDetail.Text = IsPausedGame(card) ? "Paused. Press A to carry on where you left off"
            : ContinueInfo.Visibility == Visibility.Visible ? null
            : _stats.GetValueOrDefault(card.Game.Key).Describe(DateTimeOffset.UtcNow);
        ContinueDetail.Visibility = string.IsNullOrEmpty(ContinueDetail.Text) ? Visibility.Collapsed : Visibility.Visible;

        ShowContinueArt();

        // Once the row and the tiles have their sizes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceAllGames);
    }

    /// <summary>
    /// On a TV or monitor, moves the grid down so its first row sits at the bottom of the screen, when the
    /// screen has room to spare below it. On the built-in screen the gap stays as it is.
    /// </summary>
    private void PlaceAllGames()
    {
        if (ContinuePanel.Visibility != Visibility.Visible || IsRow)
        {
            return;
        }

        var gap = AllGamesGap;
        if (_docked && Scroller.ViewportHeight > 0)
        {
            var row = TileContainer(0) is { ActualHeight: > 0 } tile ? tile.ActualHeight : TileRowHeight;
            var above = ContinuePanel.ActualHeight - AllGamesHeading.Margin.Top;
            gap = Math.Clamp(Scroller.ViewportHeight - above - row, AllGamesGap, AllGamesMaxGap);
        }

        var margin = AllGamesHeading.Margin;
        if (Math.Abs(margin.Top - gap) >= 1)
        {
            AllGamesHeading.Margin = new Thickness(margin.Left, gap, margin.Right, margin.Bottom);
        }
    }

    /// <summary>Puts the game's wide artwork on the card, or its colours when it has none.</summary>
    private async void ShowContinueArt()
    {
        if (ContinuePanel.Visibility != Visibility.Visible || ContinueCard.DataContext is not GameTile tile
            || tile.Game.Key == _continueArtKey || !IsActive)
        {
            return;
        }

        _continueArtKey = tile.Game.Key;
        var (from, to) = tile.BackdropColours;
        var colours = new LinearGradientBrush(from, to, 0);
        colours.Freeze();
        ContinueBack.Background = colours;
        ContinueArt.Source = null;

        // async void, so nothing would catch a failure here but the dispatcher: the card just keeps its colours.
        ImageSource? art;
        try
        {
            art = await Task.Run(async () =>
                await Artwork.FindAsync(tile.Game) is { } path ? GameTile.LoadCover(path, ContinueArtWidth) : null);
        }
        catch (Exception ex)
        {
            Program.Log($"Could not load the Last played artwork for {tile.Game.Key}: {ex.Message}");
            return;
        }

        // The card may have moved on to another game, or the library to the background, meanwhile.
        if (_continueArtKey == tile.Game.Key)
        {
            ContinueArt.Source = art;
        }
    }

    /// <summary>Lets go of the card's artwork while a game is in front.</summary>
    private void ClearContinueArt()
    {
        _continueArtKey = null;
        ContinueArt.Source = null;
    }

    private bool InContinueRow(DependencyObject element) =>
        ContinuePanel.Visibility == Visibility.Visible && ContinuePanel.IsAncestorOf(element);

    /// <summary>Up from the Last played card to the top bar, and down into the grid.</summary>
    private void NavigateContinue(FocusNavigationDirection direction)
    {
        switch (direction)
        {
            case FocusNavigationDirection.Up:
                FocusTopBar();
                break;
            case FocusNavigationDirection.Down when _tiles.Count > 0:
                FocusTile(0);
                break;
        }
    }

    /// <summary>Up from the grid's first row: the Last played card above it.</summary>
    private bool FocusContinueAbove()
    {
        if (ContinuePanel.Visibility != Visibility.Visible)
        {
            return false;
        }

        ContinueCard.Focus();
        return true;
    }

    /// <summary>The button whose middle is closest across the screen to the middle of another.</summary>
    private Button? Nearest(IEnumerable<Button> buttons, Button to)
    {
        double Middle(Button b) => b.TranslatePoint(new Point(b.ActualWidth / 2, 0), Scroller).X;
        var x = Middle(to);
        return buttons.MinBy(b => Math.Abs(Middle(b) - x));
    }
}

/// <summary>The games for the Last played row.</summary>
internal static class RecentGames
{
    /// <summary>The games played, most recent first, up to <paramref name="count"/> of them.</summary>
    public static List<T> Pick<T>(IEnumerable<T> games, Func<T, DateTimeOffset?> lastPlayed, int count) =>
        games.Select(game => (Game: game, Last: lastPlayed(game)))
            .Where(g => g.Last is not null)
            .OrderByDescending(g => g.Last)
            .Take(Math.Max(count, 0))
            .Select(g => g.Game)
            .ToList();
}
