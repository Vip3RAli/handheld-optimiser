using System.Globalization;
using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Ally recommended settings for a game, from the list in GameSettingsDb. The device half is copied into
/// the game's profile when the player asks, so it is switched to when the game starts and put back when
/// it closes, like any profile. The in-game half is only shown: each game keeps its settings its own way.
/// </summary>
public partial class LibraryWindow
{
    private GameRecommendation? _recommendation;

    /// <summary>Fetches a newer list in the background. The menu reads whichever list is newest when it opens.</summary>
    private async Task RefreshGameSettingsAsync()
    {
        if (await GameSettingsDb.RefreshAsync())
        {
            Program.Log("Fetched a newer list of recommended game settings");
        }
    }

    private void OnOpenRecommended(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile || GameSettingsDb.Current.For(tile.Game) is not { } recommendation)
        {
            return;
        }

        _recommendation = recommendation;
        HideMenuLists();
        _menu = RecommendedItems;
        MenuStore.Text = $"Ally recommended: {recommendation.Target}";

        RecommendedNote.Text = recommendation.Note ?? string.Empty;
        RecommendedNote.Visibility = string.IsNullOrWhiteSpace(recommendation.Note) ? Visibility.Collapsed : Visibility.Visible;

        RecommendedApplyItem.IsEnabled = recommendation.Device is not null;
        RecommendedDevice.Text = recommendation.DescribeDevice()
            + ". Saved to this game's profile: set when it starts from the library, undone when it closes.";
        RefreshRecommendedApply(tile);

        RecommendedList.ItemsSource = recommendation.Settings
            .Select(s => new GameSetting(s.Name, s.Value ?? string.Empty))
            .ToList();

        var source = recommendation.Source is { Name.Length: > 0 } from ? from.Name : "Unknown source";
        var checkedOn = DateOnly.TryParseExact(recommendation.Checked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? $", checked {day:d MMM yyyy}" : string.Empty;
        RecommendedSource.Text = recommendation.FromGuide
            ? $"From: {source}{checkedOn}"
            : $"{source}{checkedOn}. Not tested on an Ally yet, so treat it as a first try.";

        ShowMenuItems();
    }

    /// <summary>Whether the game's profile already has the recommended device settings.</summary>
    private void RefreshRecommendedApply(GameTile tile)
    {
        if (_recommendation is not { } recommendation)
        {
            return;
        }

        var profile = GameProfiles.For(tile.Game);
        RecommendedApplyItem.Tag = recommendation.Device is null ? "Not available"
            : recommendation.ApplyTo(profile) == profile ? "In the profile"
            : null;
    }

    private void OnApplyRecommended(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile || _recommendation is not { } recommendation)
        {
            return;
        }

        var profile = recommendation.ApplyTo(GameProfiles.For(tile.Game));
        if (!GameProfiles.Save(tile.Game, profile))
        {
            Notice = "The game profile could not be saved.";
            return;
        }

        Program.Log($"Saved the recommended device settings to {tile.Game.Key}'s profile");
        RefreshRecommendedApply(tile);
    }
}
