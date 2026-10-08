using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Per-game profiles: a power mode, refresh rate and brightness a game is switched to when it starts
/// from the library, with the player's own settings put back once it has closed.
/// </summary>
public partial class LibraryWindow
{
    private const string Unchanged = "Don't change";

    // A game is given this long to start before its missing process means it has closed.
    private static readonly TimeSpan ProfileStartGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProfileCheckEvery = TimeSpan.FromSeconds(10);

    private static readonly int[] ProfileBrightnessLevels = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>The settings from before a profiled game started. Only what its profile changed is set.</summary>
    private sealed record SavedSettings(Game Game, DateTime StartedAt, PowerMode? PowerMode, int? RefreshRate, int? Brightness);

    // The profile open in the menu, and the refresh rates its screen offers.
    private GameProfile _profile = GameProfile.None;
    private IReadOnlyList<int> _profileRates = [];

    private SavedSettings? _saved;
    private bool _checkingProfile;

    private void OnOpenProfile(object sender, RoutedEventArgs e)
    {
        if (_menuTile is not { } tile)
        {
            return;
        }

        _profile = GameProfiles.For(tile.Game);
        _profileRates = RefreshRates.Read()?.Available ?? [];

        HideMenuLists();
        _menu = ProfileItems;
        MenuStore.Text = "Game profile: set when it starts, undone when it closes";
        RefreshProfileItems();
        ShowMenuItems();
    }

    private void RefreshProfileItems()
    {
        var powerModes = PowerModes.Available;
        ProfilePowerSetting.IsEnabled = powerModes || _profile.PowerMode is not null;
        ProfilePowerSetting.Tag = _profile.PowerMode is { } mode ? PowerModes.Name(mode)
            : powerModes ? Unchanged
            : "Only with Windows' Balanced power plan";

        ProfileRefreshSetting.IsEnabled = _profileRates.Count > 1 || _profile.RefreshRate is not null;
        ProfileRefreshSetting.Tag = _profile.RefreshRate is { } hertz ? $"{hertz} Hz"
            : _profileRates.Count > 1 ? Unchanged
            : "This screen has one refresh rate";

        ProfileBrightnessSetting.Tag = _profile.Brightness is { } level ? $"{level}%" : Unchanged;
    }

    /// <summary>Left and right on a profile row. Each row's choices start with Don't change.</summary>
    private bool StepProfileSetting(Button row, int step)
    {
        GameProfile profile;
        if (ReferenceEquals(row, ProfilePowerSetting))
        {
            profile = _profile with { PowerMode = StepChoice(_profile.PowerMode, Enum.GetValues<PowerMode>(), step) };
        }
        else if (ReferenceEquals(row, ProfileRefreshSetting))
        {
            // A rate saved while another screen was in use stays one of the choices.
            var rates = _profileRates.ToList();
            if (_profile.RefreshRate is { } chosen && !rates.Contains(chosen))
            {
                rates.Add(chosen);
                rates.Sort();
            }

            profile = _profile with { RefreshRate = StepChoice(_profile.RefreshRate, rates, step) };
        }
        else if (ReferenceEquals(row, ProfileBrightnessSetting))
        {
            profile = _profile with { Brightness = StepChoice(_profile.Brightness, ProfileBrightnessLevels, step) };
        }
        else
        {
            return false;
        }

        if (_menuTile is { } tile && GameProfiles.Save(tile.Game, profile))
        {
            _profile = profile;
        }
        else
        {
            StatusText.Text = "The game profile could not be saved.";
        }

        RefreshProfileItems();
        return true;
    }

    private static T? StepChoice<T>(T? value, IReadOnlyList<T> choices, int step) where T : struct
    {
        var options = new List<T?> { null };
        options.AddRange(choices.Select(choice => (T?)choice));
        var index = Math.Max(0, options.IndexOf(value));
        return options[(index + step + options.Count) % options.Count];
    }

    private void OnStepProfilePower(object sender, RoutedEventArgs e) => StepProfileSetting(ProfilePowerSetting, 1);

    private void OnStepProfileRefresh(object sender, RoutedEventArgs e) => StepProfileSetting(ProfileRefreshSetting, 1);

    private void OnStepProfileBrightness(object sender, RoutedEventArgs e) => StepProfileSetting(ProfileBrightnessSetting, 1);

    /// <summary>Switches to a game's profile as it starts, keeping what it replaces.</summary>
    private void ApplyProfile(Game game, GameProfile profile)
    {
        var powerMode = profile.PowerMode is not null ? PowerModes.Read() : null;
        var refreshRate = profile.RefreshRate is not null ? RefreshRates.Read()?.Current : null;
        _saved = new SavedSettings(game, DateTime.UtcNow, powerMode, refreshRate, null);

        if (profile.PowerMode is { } mode && powerMode is not null && powerMode != mode && !PowerModes.Set(mode))
        {
            Program.Log($"Could not switch to the power mode in {game.Key}'s profile");
        }

        if (profile.RefreshRate is { } hertz && refreshRate is not null && refreshRate != hertz && RefreshRates.Set(hertz) is { } error)
        {
            Program.Log($"Could not switch to the refresh rate in {game.Key}'s profile: {error}");
        }

        if (profile.Brightness is { } level)
        {
            _ = ApplyProfileBrightnessAsync(game, level);
        }

        Program.Log($"Switched to {game.Key}'s profile");
    }

    private async Task ApplyProfileBrightnessAsync(Game game, int level)
    {
        // WMI is slow, so the reading and the change both happen off the UI thread.
        var before = await Task.Run(() => Brightness.Read() is { } now && Brightness.Set(level) ? now : (int?)null);
        if (before is not null && _saved is { } saved && saved.Game == game)
        {
            _saved = saved with { Brightness = before };
        }
    }

    /// <summary>Puts back the settings a profiled game replaced, if any.</summary>
    private void RestoreProfileSettings()
    {
        if (_saved is not { } saved)
        {
            return;
        }

        _saved = null;
        _profileCheck.Stop();

        if (saved.PowerMode is { } mode)
        {
            PowerModes.Set(mode);
        }

        if (saved.RefreshRate is { } hertz && RefreshRates.Set(hertz) is { } error)
        {
            Program.Log($"Could not put the refresh rate back: {error}");
        }

        if (saved.Brightness is { } level)
        {
            _ = Task.Run(() => Brightness.Set(level));
        }

        Program.Log($"Put back the settings {saved.Game.Key}'s profile replaced");
    }

    /// <summary>
    /// Puts the player's settings back once the profiled game has closed. While it still runs (the
    /// player pressed the home button mid-game) it looks again every few seconds the library is in front.
    /// </summary>
    private async Task CheckProfileRestoreAsync()
    {
        if (_saved is not { } saved || _checkingProfile)
        {
            return;
        }

        _checkingProfile = true;
        try
        {
            var running = await Task.Run(() => IsRunning(saved.Game));
            if (!ReferenceEquals(_saved, saved))
            {
                return;
            }

            if (running || DateTime.UtcNow - saved.StartedAt < ProfileStartGrace)
            {
                if (IsActive)
                {
                    _profileCheck.Start();
                }

                return;
            }

            RestoreProfileSettings();
            StatusText.Text = $"Your own settings are back after {saved.Game.Title}.";
        }
        finally
        {
            _checkingProfile = false;
        }
    }

    private void OnProfileCheck()
    {
        _profileCheck.Stop();
        _ = CheckProfileRestoreAsync();
    }

    /// <summary>
    /// Whether anything is running from the game's folder. Store games are started through their store,
    /// so there is no process of ours to watch; the game's own exe sits in its install folder.
    /// </summary>
    private static bool IsRunning(Game game)
    {
        var folder = game.InstallDirectory ?? Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var running = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                running = running || (Native.ProcessPath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
        }

        return running;
    }
}
