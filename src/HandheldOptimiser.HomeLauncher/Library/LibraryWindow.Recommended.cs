using System.Windows;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Optimised settings for a game, from the list in GameSettingsDb. The device half is copied into
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
        MenuStore.Text = $"Optimised for {recommendation.Target}";

        RecommendedNote.Text = recommendation.Note ?? string.Empty;
        RecommendedNote.Visibility = string.IsNullOrWhiteSpace(recommendation.Note) ? Visibility.Collapsed : Visibility.Visible;

        RecommendedApplyItem.IsEnabled = recommendation.Device is not null;
        RecommendedDevice.Text = recommendation.DescribeDevice();
        RefreshRecommendedApply(tile);

        RecommendedList.ItemsSource = recommendation.Settings
            .Select(s => new GameSetting(s.Name, s.Value ?? string.Empty))
            .ToList();

        RecommendedSource.Text = recommendation.FromGuide && recommendation.Source is { Name.Length: > 0 } named
            ? $"Source: {named.Name}"
            : "A first try, not from a guide";

        ShowMenuItems();
    }

    /// <summary>The device half for this screen: sizes and rates it does not offer are left out.</summary>
    private static GameProfile ApplyForThisScreen(GameRecommendation recommendation, GameProfile profile) =>
        recommendation.ApplyTo(profile,
            Resolutions.Read()?.Available is { Count: > 0 } sizes ? sizes : null,
            RefreshRates.Read()?.Available is { Count: > 0 } rates ? rates : null);

    /// <summary>Whether the game's profile already has the recommended device settings.</summary>
    private void RefreshRecommendedApply(GameTile tile)
    {
        if (_recommendation is not { } recommendation)
        {
            return;
        }

        var profile = GameProfiles.For(tile.Game);
        RecommendedApplyItem.Tag = recommendation.Device is null ? "Not available"
            : ApplyForThisScreen(recommendation, profile) == profile ? "Applied"
            : null;
    }

    private void OnApplyRecommended(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile || _recommendation is not { } recommendation)
        {
            return;
        }

        var profile = ApplyForThisScreen(recommendation, GameProfiles.For(tile.Game));
        if (!GameProfiles.Save(tile.Game, profile))
        {
            Notice = "The game profile could not be saved.";
            return;
        }

        Program.Log($"Saved the optimised device settings to {tile.Game.Key}'s profile");
        Notice = $"Optimised settings saved to {tile.Game.Title}'s profile.";
        RefreshRecommendedApply(tile);
    }
}
