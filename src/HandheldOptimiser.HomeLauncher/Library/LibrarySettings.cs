using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What is behind the grid while a game has the focus.</summary>
internal enum BackgroundKind
{
    Colours,
    Artwork,
    Plain
}

/// <summary>How much of a colour gradient shows through the shade that keeps the text readable.</summary>
internal enum ColourStrength
{
    Subtle,
    Medium,
    Strong
}

/// <summary>Which end of the screen the artwork sits at; the game's colours fill the rest.</summary>
internal enum ArtworkPosition
{
    Top,
    Bottom
}

/// <summary>How soft the artwork behind the grid is.</summary>
internal enum ArtworkBlur
{
    Off,
    Light,
    Medium,
    Strong
}

/// <summary>
/// The library's own preferences, changed from its settings menu. They live in the per-user key the main
/// app writes the home app choice to, so the background options on its Full Screen Mode page and the
/// ones here are the same settings.
/// </summary>
internal static class LibrarySettings
{
    // Keep the names in step with HomeAppRegistration in the main app.
    private const string BackgroundValue = "Background";
    private const string StrengthValue = "ColourStrength";
    private const string ArtworkKeyValue = "SteamGridDbKey";
    private const string BlurValue = "ArtworkBlur";
    private const string PositionValue = "ArtworkPosition";
    private const string StatusValue = "StatusBar";
    private const string FilterValue = "StoreFilter";
    private const string QuickActionsValue = "QuickActions";
    private const string ShowHiddenValue = "ShowHiddenGames";

    public static BackgroundKind Background
    {
        get => Read(BackgroundValue)?.ToLowerInvariant() switch
        {
            "artwork" => BackgroundKind.Artwork,
            "none" => BackgroundKind.Plain,
            _ => BackgroundKind.Colours
        };
        set => Write(BackgroundValue, value switch
        {
            BackgroundKind.Artwork => "artwork",
            BackgroundKind.Plain => "none",
            _ => "gradient"
        });
    }

    public static ColourStrength Strength
    {
        get => Read(StrengthValue)?.ToLowerInvariant() switch
        {
            "subtle" => ColourStrength.Subtle,
            "strong" => ColourStrength.Strong,
            _ => ColourStrength.Medium
        };
        set => Write(StrengthValue, value.ToString().ToLowerInvariant());
    }

    public static ArtworkBlur Blur
    {
        get => Enum.TryParse<ArtworkBlur>(Read(BlurValue), ignoreCase: true, out var blur) && Enum.IsDefined(blur)
            ? blur
            : ArtworkBlur.Light;
        set => Write(BlurValue, value.ToString().ToLowerInvariant());
    }

    public static ArtworkPosition Position
    {
        get => string.Equals(Read(PositionValue), "bottom", StringComparison.OrdinalIgnoreCase)
            ? ArtworkPosition.Bottom
            : ArtworkPosition.Top;
        set => Write(PositionValue, value.ToString().ToLowerInvariant());
    }

    /// <summary>Battery and Wi-Fi beside the clock.</summary>
    public static bool ShowStatus
    {
        get => ReadSwitch(StatusValue);
        set => WriteSwitch(StatusValue, value);
    }

    /// <summary>The store tabs under the title, and the bumpers that move between them.</summary>
    public static bool ShowFilter
    {
        get => ReadSwitch(FilterValue);
        set => WriteSwitch(FilterValue, value);
    }

    /// <summary>The per-game menu on X, or press and hold.</summary>
    public static bool QuickActions
    {
        get => ReadSwitch(QuickActionsValue);
        set => WriteSwitch(QuickActionsValue, value);
    }

    /// <summary>Games the player hid, shown faded so they can be brought back. Off until switched on.</summary>
    public static bool ShowHidden
    {
        get => ReadSwitch(ShowHiddenValue, on: false);
        set => WriteSwitch(ShowHiddenValue, value);
    }

    // Most switches are on until the player switches them off.
    private static bool ReadSwitch(string name, bool on = true) => Read(name)?.ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => on
    };

    private static void WriteSwitch(string name, bool on) => Write(name, on ? "on" : "off");

    /// <summary>The player's SteamGridDB API key, or null when none is set.</summary>
    public static string? ArtworkKey
    {
        get => Read(ArtworkKeyValue)?.Trim() is { Length: > 0 } key ? key : null;
        set => Write(ArtworkKeyValue, value);
    }

    private static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Program.SettingsKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void Write(string name, string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Program.SettingsKey);
            if (string.IsNullOrEmpty(value))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(name, value, RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The setting still applies until the library closes; it is just not remembered.
            Program.Log($"Could not save the {name} setting: {ex.Message}");
        }
    }
}
