using System.IO;
using System.Text.Json;
using HandheldOptimiser.PowerService;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The settings a game is played with, switched to when it starts from the library. A null part leaves
/// that setting as it is.
/// </summary>
/// <param name="CloseApps">Whether to close the background programs for this game, whatever the library's
/// own setting. Null follows that setting.</param>
/// <param name="Resolution">The screen size to play at, such as 1280 x 720 to stretch the battery.</param>
/// <param name="AutoTdpFps">The frame rate TDP Engine holds this game at, whatever the library's own setting:
/// 0 for off. Null follows that setting.</param>
/// <param name="FanCurve">The fan curve for this game, one of <see cref="AutoTdp.FanChoices"/>, whatever the
/// library's own setting. Null follows that setting.</param>
/// <param name="CpuBoostMode">The CPU boost mode for this game, one of <see cref="AutoTdp.CpuBoostModes"/>.</param>
/// <param name="AcBoostBursts">Whether short AC boost bursts (sPPT/fPPT) are allowed while playing on AC.</param>
/// <param name="FreeSync">Whether AMD FreeSync VRR is enabled for this game.</param>
/// <param name="MaxWatts">Optional maximum TDP ceiling (7 to 30 W) for TDP Engine during this game.</param>
/// <param name="ControllerMode">Optional Ally controller mode (<see cref="AutoTdp.ControllerModes"/>) held during this game.</param>
internal sealed record GameProfile(PowerMode? PowerMode, int? RefreshRate, int? Brightness, bool? CloseApps = null,
    Resolution? Resolution = null, int? AutoTdpFps = null, string? FanCurve = null, string? CpuBoostMode = null,
    bool? AcBoostBursts = null, bool? FreeSync = null, int? MaxWatts = null, string? ControllerMode = null)
{
    public static readonly GameProfile None = new(null, null, null);

    /// <summary>Whether it changes any of the device or service settings applied and restored around the game.</summary>
    public bool IsEmpty => PowerMode is null && RefreshRate is null && Brightness is null && Resolution is null
        && CpuBoostMode is null && AcBoostBursts is null && FreeSync is null && ControllerMode is null;

    /// <summary>Whether this profile configures any per-game override, including TDP Engine or close-apps behaviour.</summary>
    public bool HasAny => !IsEmpty || CloseApps is not null || AutoTdpFps is not null || FanCurve is not null || MaxWatts is not null;

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

        if (AutoTdpFps is { } fps)
        {
            parts.Add(fps > 0 ? $"TDP Engine at {fps} fps" : "TDP Engine off");
        }

        if (MaxWatts is { } watts)
        {
            parts.Add($"{watts} W max");
        }

        if (FanCurve is { } fans)
        {
            parts.Add($"{AutoTdp.FanName(fans)} fans");
        }

        if (CpuBoostMode is { } boost)
        {
            parts.Add($"CPU boost {AutoTdp.CpuBoostName(boost).ToLowerInvariant()}");
        }

        if (AcBoostBursts is { } acBoost)
        {
            parts.Add(acBoost ? "AC boost bursts on" : "AC boost bursts off");
        }

        if (FreeSync is { } freeSync)
        {
            parts.Add(freeSync ? "FreeSync on" : "FreeSync off");
        }

        if (ControllerMode is { } controls)
        {
            parts.Add($"{AutoTdp.ControllerModeName(controls).ToLowerInvariant()} controls");
        }

        return parts.Count == 0 ? "Not set. Plays with your current settings" : string.Join(", ", parts);
    }
}

/// <summary>Each game's profile, one value per game key, as "power=performance;res=1280x720;hz=60;brightness=50;fps=60;fan=quiet".</summary>
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
        int? autoTdp = null;
        string? fans = null;
        string? boost = null;
        bool? acBoost = null;
        bool? freeSync = null;
        int? maxWatts = null;
        string? controls = null;

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
                case "fps" when value == "off":
                    autoTdp = 0;
                    break;
                case "fps" when int.TryParse(value, out var parsed) && AutoTdp.FrameRates.Contains(parsed):
                    autoTdp = parsed;
                    break;
                case "fan" when AutoTdp.FanChoices.Contains(value):
                    fans = value;
                    break;
                case "boost":
                {
                    var normalized = value.ToLowerInvariant();
                    if (AutoTdp.CpuBoostModes.Contains(normalized))
                    {
                        boost = normalized;
                    }
                    break;
                }
                case "acboost" when value is "on" or "off":
                    acBoost = value == "on";
                    break;
                case "freesync" when value is "on" or "off":
                    freeSync = value == "on";
                    break;
                case "watts" when int.TryParse(value, out var parsed)
                    && parsed is >= AutoTdpProtocol.LowestWatts and <= AutoTdpProtocol.HighestWatts:
                    maxWatts = parsed;
                    break;
                case "controls":
                {
                    var normalized = value.ToLowerInvariant();
                    if (AutoTdp.ControllerModes.Contains(normalized))
                    {
                        controls = normalized;
                    }
                    break;
                }
            }
        }

        return new GameProfile(mode, hertz, brightness, closeApps, resolution, autoTdp, fans,
            boost, acBoost, freeSync, maxWatts, controls);
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

        if (profile.AutoTdpFps is { } fps)
        {
            parts.Add(fps > 0 ? $"fps={fps}" : "fps=off");
        }

        if (profile.FanCurve is { } fans)
        {
            parts.Add($"fan={fans}");
        }

        if (profile.CpuBoostMode is { } boost)
        {
            parts.Add($"boost={boost}");
        }

        if (profile.AcBoostBursts is { } acBoost)
        {
            parts.Add($"acboost={(acBoost ? "on" : "off")}");
        }

        if (profile.FreeSync is { } freeSync)
        {
            parts.Add($"freesync={(freeSync ? "on" : "off")}");
        }

        if (profile.MaxWatts is { } watts)
        {
            parts.Add($"watts={watts}");
        }

        if (profile.ControllerMode is { } controls)
        {
            parts.Add($"controls={controls}");
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

    public static string DefaultPresetsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "HandheldOptimiser", "Presets");

    /// <summary>Exports a game's profile to a shareable JSON preset file and returns the saved path.</summary>
    public static string? ExportPreset(Game game, GameProfile profile, string? folder = null)
    {
        var formatted = Format(profile);
        if (formatted.Length == 0)
        {
            return null;
        }

        var dir = folder ?? DefaultPresetsFolder;
        try
        {
            Directory.CreateDirectory(dir);
            var safeTitle = string.Concat(game.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            if (safeTitle.Length == 0)
            {
                safeTitle = "game";
            }

            var filePath = Path.Combine(dir, $"{safeTitle}.preset.json");
            var payload = new
            {
                title = game.Title,
                key = game.Key,
                profile = formatted
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return filePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not export preset for {game.Key}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses a preset from either a JSON preset file or a compact profile string
    /// (<c>power=performance;res=1920x1080;hz=60</c>).
    /// </summary>
    public static GameProfile? ImportPreset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (File.Exists(trimmed))
        {
            try
            {
                trimmed = File.ReadAllText(trimmed).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("profile", out var p) && p.GetString() is { Length: > 0 } profileStr)
                {
                    var parsed = Parse(profileStr);
                    return Format(parsed).Length > 0 ? parsed : null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var direct = Parse(trimmed);
        return Format(direct).Length > 0 ? direct : null;
    }
}
