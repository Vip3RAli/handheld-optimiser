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

/// <summary>The order of the games in the grid. Favourites always come first.</summary>
internal enum SortOrder
{
    RecentlyPlayed,
    MostPlayed,
    Name
}

/// <summary>How the games are laid out: a grid that scrolls down, or one row that scrolls across.</summary>
internal enum LibraryLayout
{
    Grid,
    Row
}

/// <summary>The layout on a TV or monitor: the handheld's own, or one of the two.</summary>
internal enum DockedLayout
{
    Same,
    Grid,
    Row
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
    private const string SortValue = "SortOrder";
    private const string GameModeValue = "GameMode";
    private const string ReopenValue = "ReopenClosedApps";
    private const string StoreClosingValue = "CloseStoreApps";
    private const string LayoutValue = "Layout";
    private const string ContinueValue = "ContinuePlaying";
    private const string ResumeValue = "ResumeAfterSleep";
    private const string DockedValue = "DockedMode";
    private const string DockedLayoutValue = "DockedLayout";
    private const string DockedSizeValue = "DockedSize";
    private const string NotInstalledValue = "ShowNotInstalled";
    private const string SteamKeyValue = "SteamWebApiKey";

    /// <summary>The sizes a TV or monitor can show the library at, as percentages. 0 fits it to the screen.</summary>
    public static readonly int[] DockedSizes = [0, 100, 125, 150, 175, 200];

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

    public static SortOrder Sort
    {
        get => Enum.TryParse<SortOrder>(Read(SortValue), ignoreCase: true, out var sort) && Enum.IsDefined(sort)
            ? sort
            : SortOrder.RecentlyPlayed;
        set => Write(SortValue, value.ToString().ToLowerInvariant());
    }

    /// <summary>Close the chosen background programs whenever a game starts. Off until switched on.</summary>
    public static bool GameMode
    {
        get => ReadSwitch(GameModeValue, on: false);
        set => WriteSwitch(GameModeValue, value);
    }

    /// <summary>Start the programs closed for a game again once it has closed.</summary>
    public static bool ReopenApps
    {
        get => ReadSwitch(ReopenValue);
        set => WriteSwitch(ReopenValue, value);
    }

    /// <summary>Whether a store's app is closed after one of its games.</summary>
    public static StoreClosing StoreClosing
    {
        get => Enum.TryParse<StoreClosing>(Read(StoreClosingValue), ignoreCase: true, out var closing) && Enum.IsDefined(closing)
            ? closing
            : StoreClosing.Off;
        set => Write(StoreClosingValue, value.ToString().ToLowerInvariant());
    }

    public static LibraryLayout Layout
    {
        get => Enum.TryParse<LibraryLayout>(Read(LayoutValue), ignoreCase: true, out var layout) && Enum.IsDefined(layout)
            ? layout
            : LibraryLayout.Grid;
        set => Write(LayoutValue, value.ToString().ToLowerInvariant());
    }

    /// <summary>The last game played, and the few before it, above the grid.</summary>
    public static bool ContinuePlaying
    {
        get => ReadSwitch(ContinueValue);
        set => WriteSwitch(ContinueValue, value);
    }

    /// <summary>Back to the game that was running, rather than the library, when the device wakes.</summary>
    public static bool ResumeAfterSleep
    {
        get => ReadSwitch(ResumeValue);
        set => WriteSwitch(ResumeValue, value);
    }

    /// <summary>A layout and size of its own while the library is on a TV or monitor.</summary>
    public static bool DockedMode
    {
        get => ReadSwitch(DockedValue);
        set => WriteSwitch(DockedValue, value);
    }

    public static DockedLayout DockedLayout
    {
        get => Enum.TryParse<DockedLayout>(Read(DockedLayoutValue), ignoreCase: true, out var layout) && Enum.IsDefined(layout)
            ? layout
            : DockedLayout.Row;
        set => Write(DockedLayoutValue, value.ToString().ToLowerInvariant());
    }

    /// <summary>One of <see cref="DockedSizes"/>; 0, the default, fits the library to the screen.</summary>
    public static int DockedSize
    {
        get => int.TryParse(Read(DockedSizeValue), out var size) && DockedSizes.Contains(size) ? size : 0;
        set => Write(DockedSizeValue, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Games the player owns but has not installed, in a tab of their own.</summary>
    public static bool ShowNotInstalled
    {
        get => ReadSwitch(NotInstalledValue);
        set => WriteSwitch(NotInstalledValue, value);
    }

    /// <summary>The player's Steam Web API key, which lists the Steam games they own. Null when none is set.</summary>
    public static string? SteamKey
    {
        get => Read(SteamKeyValue)?.Trim() is { Length: > 0 } key ? key : null;
        set => Write(SteamKeyValue, value);
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
