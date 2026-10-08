using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The settings a game is played with, switched to when it starts from the library. A null part leaves
/// that setting as it is.
/// </summary>
/// <param name="CloseApps">Whether to close the background programs for this game, whatever the library's
/// own setting. Null follows that setting.</param>
/// <param name="Resolution">The screen size to play at, such as 1280 x 720 to stretch the battery.</param>
internal sealed record GameProfile(PowerMode? PowerMode, int? RefreshRate, int? Brightness, bool? CloseApps = null,
    Resolution? Resolution = null)
{
    public static readonly GameProfile None = new(null, null, null);

    /// <summary>Whether it changes any of the device's settings, which are put back after the game.</summary>
    public bool IsEmpty => PowerMode is null && RefreshRate is null && Brightness is null && Resolution is null;

    /// <summary>A one-line summary for the quick actions menu.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (PowerMode is { } mode)
        {
            parts.Add(PowerModes.Name(mode));
        }

        if (Resolution is { } size)
        {
            parts.Add(size.ToString());
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

/// <summary>Each game's profile, one value per game key, as "power=performance;res=1280x720;hz=60;brightness=50".</summary>
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

        return string.IsNullOrWhiteSpace(text) ? GameProfile.None : Parse(text);
    }

    /// <summary>A profile from its stored text. Parts that cannot be read are left as Don't change.</summary>
    public static GameProfile Parse(string text)
    {
        PowerMode? mode = null;
        Resolution? resolution = null;
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
                case "res" when Resolution.Parse(value) is { } parsed:
                    resolution = parsed;
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

        return new GameProfile(mode, hertz, brightness, closeApps, resolution);
    }

    /// <summary>A profile as stored text, empty when it changes nothing.</summary>
    public static string Format(GameProfile profile)
    {
        var parts = new List<string>();
        if (profile.PowerMode is { } mode)
        {
            parts.Add($"power={mode.ToString().ToLowerInvariant()}");
        }

        if (profile.Resolution is { } size)
        {
            parts.Add($"res={size.Key}");
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

        return string.Join(';', parts);
    }

    /// <returns>False when the profile could not be stored.</returns>
    public static bool Save(Game game, GameProfile profile)
    {
        var text = Format(profile);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProfilesKey);
            if (text.Length == 0)
            {
                key.DeleteValue(game.Key, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(game.Key, text, RegistryValueKind.String);
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
