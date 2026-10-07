using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The settings a game is played with, switched to when it starts from the library. A null part leaves
/// that setting as it is.
/// </summary>
/// <param name="CloseApps">Whether to close the background programs for this game, whatever the library's
/// own setting. Null follows that setting.</param>
internal sealed record GameProfile(PowerMode? PowerMode, int? RefreshRate, int? Brightness, bool? CloseApps = null)
{
    public static readonly GameProfile None = new(null, null, null);

    /// <summary>Whether it changes any of the device's settings, which are put back after the game.</summary>
    public bool IsEmpty => PowerMode is null && RefreshRate is null && Brightness is null;

    /// <summary>A one-line summary for the quick actions menu.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (PowerMode is { } mode)
        {
            parts.Add(PowerModes.Name(mode));
        }

        if (RefreshRate is { } hertz)
        {
            parts.Add($"{hertz} Hz");
        }

        if (Brightness is { } brightness)
        {
            parts.Add($"{brightness}% brightness");
        }

        if (CloseApps is { } close)
        {
            parts.Add(close ? "closes background programs" : "leaves background programs running");
        }

        return parts.Count == 0 ? "Not set. Plays with your current settings" : string.Join(", ", parts);
    }
}

/// <summary>Each game's profile, one value per game key, as "power=performance;hz=60;brightness=50".</summary>
internal static class GameProfiles
{
    private const string ProfilesKey = Program.SettingsKey + @"\Profiles";

    public static GameProfile For(Game game)
    {
        string? text;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProfilesKey);
            text = key?.GetValue(game.Key) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return GameProfile.None;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return GameProfile.None;
        }

        PowerMode? mode = null;
        int? hertz = null;
        int? brightness = null;
        bool? closeApps = null;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (name, value) = part.Split('=', 2, StringSplitOptions.TrimEntries) is [var n, var v] ? (n, v) : (part, "");
            switch (name.ToLowerInvariant())
            {
                case "power" when Enum.TryParse<PowerMode>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed):
                    mode = parsed;
                    break;
                case "hz" when int.TryParse(value, out var parsed) && parsed > 1:
                    hertz = parsed;
                    break;
                case "brightness" when int.TryParse(value, out var parsed) && parsed is >= 0 and <= 100:
                    brightness = parsed;
                    break;
                case "closeapps" when value is "on" or "off":
                    closeApps = value == "on";
                    break;
            }
        }

        return new GameProfile(mode, hertz, brightness, closeApps);
    }

    /// <returns>False when the profile could not be stored.</returns>
    public static bool Save(Game game, GameProfile profile)
    {
        var parts = new List<string>();
        if (profile.PowerMode is { } mode)
        {
            parts.Add($"power={mode.ToString().ToLowerInvariant()}");
        }

        if (profile.RefreshRate is { } hertz)
        {
            parts.Add($"hz={hertz}");
        }

        if (profile.Brightness is { } brightness)
        {
            parts.Add($"brightness={brightness}");
        }

        if (profile.CloseApps is { } close)
        {
            parts.Add($"closeapps={(close ? "on" : "off")}");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProfilesKey);
            if (parts.Count == 0)
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, string.Join(';', parts), RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save the profile for {game.Key}: {ex.Message}");
            return false;
        }
    }
}
