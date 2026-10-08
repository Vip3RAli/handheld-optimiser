using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Continue playing: the last game played as a wide card above the grid, with the few played before it
/// beside it, so they are a press or two away whatever order the grid is in.
/// </summary>
public partial class LibraryWindow
{
    // The card's artwork is decoded at about the card's width at 200% scaling.
    private const int ContinueArtWidth = 1120;

    // The card and the tiles beside it, in DIPs including their margins, as LibraryWindow.xaml draws them.
    private const double ContinueCardWidth = 580;
    private const double TileWidth = 200;

    private bool _continuePlaying = LibrarySettings.ContinuePlaying;

    // Whether the focus is in the Continue playing row rather than the grid, to put it back there.
    private bool _focusInContinue;

    // The game whose artwork is on the card, or on its way there.
    private string? _continueArtKey;

    /// <summary>Fills the Continue playing row, or hides it when it is off or does not apply.</summary>
    private void UpdateContinue()
    {
        var played = _continuePlaying && _layout == LibraryLayout.Grid && _filter == TileFilter.All
            ? RecentGames.Pick(ShownTiles().Where(t => t.IsInstalled && !t.IsHidden),
                t => _stats.GetValueOrDefault(t.Game.Key).LastPlayed, 1 + RecentTileCount())
            : [];

        if (played.Count == 0)
        {
            ContinuePanel.Visibility = Visibility.Collapsed;
            _focusInContinue = false;
            return;
        }

        var card = played[0];
        ContinuePanel.Visibility = Visibility.Visible;
        ContinueCard.DataContext = card;
        ContinueDetail.Text = IsPausedGame(card) ? "Paused. Press A to carry on where you left off"
            : _stats.GetValueOrDefault(card.Game.Key).Describe(DateTimeOffset.UtcNow);

        // Rebuilding the tiles would lose the focus on one of them, so only when they changed.
        var recent = played.Skip(1).ToList();
        if (RecentTiles.ItemsSource is not List<GameTile> shown || !shown.SequenceEqual(recent))
        {
            RecentTiles.ItemsSource = recent;
        }

        ShowContinueArt();
    }

    /// <summary>How many of the earlier games fit beside the card on this screen.</summary>
    private int RecentTileCount()
    {
        var width = Scroller.ActualWidth - Scroller.Padding.Left - Scroller.Padding.Right;
        return width <= 0 ? 3 : Math.Clamp((int)((width - ContinueCardWidth) / TileWidth), 0, 8);
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

        var art = await Task.Run(async () =>
            await Artwork.FindAsync(tile.Game) is { } path ? GameTile.LoadCover(path, ContinueArtWidth) : null);

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

    /// <summary>The card and the tiles beside it, left to right, while the row is showing.</summary>
    private List<Button> ContinueButtons()
    {
        if (ContinuePanel.Visibility != Visibility.Visible)
        {
            return [];
        }

        var buttons = new List<Button> { ContinueCard };
        for (var i = 0; i < RecentTiles.Items.Count; i++)
        {
            if (RecentTiles.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container
                && VisualChild<Button>(container) is { } button)
            {
                buttons.Add(button);
            }
        }

        return buttons;
    }

    private bool InContinueRow(DependencyObject element) =>
        ContinuePanel.Visibility == Visibility.Visible && ContinuePanel.IsAncestorOf(element);

    /// <summary>Left and right along the Continue playing row, and down into the grid.</summary>
    private void NavigateContinue(Button from, FocusNavigationDirection direction)
    {
        var buttons = ContinueButtons();
        var index = buttons.IndexOf(from);
        switch (direction)
        {
            case FocusNavigationDirection.Left when index > 0:
                buttons[index - 1].Focus();
                break;
            case FocusNavigationDirection.Right when index >= 0 && index + 1 < buttons.Count:
                buttons[index + 1].Focus();
                break;
            case FocusNavigationDirection.Down when _tiles.Count > 0:
                var firstRow = Enumerable.Range(0, Math.Min(ColumnCount(), _tiles.Count))
                    .Select(i => TileContainer(i) is { } c ? VisualChild<Button>(c) : null)
                    .OfType<Button>()
                    .ToList();
                (Nearest(firstRow, from) ?? firstRow.FirstOrDefault())?.Focus();
                break;
        }
    }

    /// <summary>Up from the grid's first row: the card or tile in the Continue playing row above it.</summary>
    private bool FocusContinueAbove(Button from)
    {
        if (Nearest(ContinueButtons(), from) is not { } above)
        {
            return false;
        }

        above.Focus();
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

/// <summary>The games for the Continue playing row.</summary>
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
