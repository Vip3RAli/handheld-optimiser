using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>What is behind the grid while a game has the focus.</summary>
internal enum BackgroundKind
{
    Colours,
    Artwork,
    Green,
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

/// <summary>
/// How the games are laid out: a grid that scrolls down, one row that scrolls across, or Old School, a
/// staggered line of big cards like the Xbox 360's.
/// </summary>
internal enum LibraryLayout
{
    Grid,
    Row,
    OldSchool
}

/// <summary>The layout on a TV or monitor.</summary>
internal enum DockedLayout
{
    Grid,
    Row,
    OldSchool
}

/// <summary>
/// The library's own preferences, changed from its settings menu. They live in the per-user key the main
/// app writes the home app choice to.
/// </summary>
internal static class LibrarySettings
{
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
    private const string ContinueInfoValue = "ContinueInfo";
    private const string StoreDetailsValue = "StoreDetails";
    private const string ResumeValue = "ResumeAfterSleep";
    private const string QuickResumeValue = "QuickResume";
    private const string PausedValue = "PausedProcesses";
    private const string DockedValue = "DockedMode";
    private const string DockedLayoutValue = "DockedLayout";
    private const string DockedSizeValue = "DockedSize";
    private const string NotInstalledValue = "ShowNotInstalled";
    private const string SteamKeyValue = "SteamWebApiKey";
    private const string DealsValue = "ShowDeals";
    private const string EpicFreeValue = "EpicFreeGames";
    private const string DealsKeyValue = "IsThereAnyDealKey";
    private const string WishlistDealsValue = "WishlistDealsFirst";
    private const string AutoTdpFpsValue = "AutoTdpFps";
    private const string AutoTdpTemperatureValue = "AutoTdpTemperature";
    private const string AutoTdpWattsValue = "AutoTdpMaxWatts";
    private const string FanCurveValue = "FanCurve";

    /// <summary>The sizes a TV or monitor can show the library at, as percentages. 0 fits it to the screen.</summary>
    public static readonly int[] DockedSizes = [0, 100, 125, 150, 175, 200];

    public static BackgroundKind Background
    {
        get => Read(BackgroundValue)?.ToLowerInvariant() switch
        {
            "artwork" => BackgroundKind.Artwork,
            "green" => BackgroundKind.Green,
            "none" => BackgroundKind.Plain,
            _ => BackgroundKind.Colours
        };
        set => Write(BackgroundValue, value switch
        {
            BackgroundKind.Artwork => "artwork",
            BackgroundKind.Green => "green",
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

    /// <summary>The last game played, above the grid.</summary>
    public static bool ContinuePlaying
    {
        get => ReadSwitch(ContinueValue);
        set => WriteSwitch(ContinueValue, value);
    }

    /// <summary>The last game's play time, size and profile, beside its Last played card.</summary>
    public static bool ContinueInfo
    {
        get => ReadSwitch(ContinueInfoValue);
        set => WriteSwitch(ContinueInfoValue, value);
    }

    /// <summary>The last game's description and the like from Steam's store or Wikipedia. Off until switched on.</summary>
    public static bool StoreDetails
    {
        get => ReadSwitch(StoreDetailsValue, on: false);
        set => WriteSwitch(StoreDetailsValue, value);
    }

    /// <summary>Back to the game that was running, rather than the library, when the device wakes.</summary>
    public static bool ResumeAfterSleep
    {
        get => ReadSwitch(ResumeValue);
        set => WriteSwitch(ResumeValue, value);
    }

    /// <summary>Pause the game being played while the library is in front, until it is picked again.</summary>
    public static bool QuickResume
    {
        get => ReadSwitch(QuickResumeValue);
        set => WriteSwitch(QuickResumeValue, value);
    }

    /// <summary>The processes paused right now, for the next library to resume if this one ends without doing so.</summary>
    public static string? PausedProcesses
    {
        get => Read(PausedValue);
        set => Write(PausedValue, value);
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
        get => ReadSecret(Store, SteamKeyValue);
        set => WriteSecret(Store, SteamKeyValue, value);
    }

    /// <summary>The Deals row below the library. Off until switched on.</summary>
    public static bool ShowDeals
    {
        get => ReadSwitch(DealsValue, on: false);
        set => WriteSwitch(DealsValue, value);
    }

    /// <summary>The Epic Games Store's free games of the week at the front of the Deals row, which need no key.</summary>
    public static bool EpicFreeGames
    {
        get => ReadSwitch(EpicFreeValue);
        set => WriteSwitch(EpicFreeValue, value);
    }

    /// <summary>The player's IsThereAnyDeal API key, for the sales in the Deals row. Null when none is set.</summary>
    public static string? DealsKey
    {
        get => ReadSecret(Store, DealsKeyValue);
        set => WriteSecret(Store, DealsKeyValue, value);
    }

    /// <summary>Deals on the games on the player's Steam wishlist first, which needs the Steam key too.</summary>
    public static bool WishlistDeals
    {
        get => ReadSwitch(WishlistDealsValue);
        set => WriteSwitch(WishlistDealsValue, value);
    }

    /// <summary>The frame rate TDP Engine holds games at, one of <see cref="AutoTdp.FrameRates"/>, or 0, the default, for off.</summary>
    public static int AutoTdpFps
    {
        get => ReadChoice(AutoTdpFpsValue, AutoTdp.FrameRates, 0);
        set => Write(AutoTdpFpsValue, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The temperature TDP Engine lowers the power limit at, whatever the frame rate.</summary>
    public static int AutoTdpTemperature
    {
        get => ReadChoice(AutoTdpTemperatureValue, AutoTdp.TemperatureLimits, 80);
        set => Write(AutoTdpTemperatureValue, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The highest power limit TDP Engine may set, in watts.</summary>
    public static int AutoTdpMaxWatts
    {
        get => ReadChoice(AutoTdpWattsValue, AutoTdp.MostWatts, 25);
        set => Write(AutoTdpWattsValue, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The fan curve games are played with, one of <see cref="AutoTdp.FanChoices"/>; the Ally's own by default.</summary>
    public static string FanCurve
    {
        get => Read(FanCurveValue) is { } fans && AutoTdp.FanChoices.Contains(fans) ? fans : AutoTdp.AllyFans;
        set => Write(FanCurveValue, value == AutoTdp.AllyFans ? null : value);
    }

    private static int ReadChoice(string name, int[] choices, int fallback) =>
        int.TryParse(Read(name), out var value) && (choices.Contains(value) || value == 0 && fallback == 0) ? value : fallback;

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
        get => ReadSecret(Store, ArtworkKeyValue);
        set => WriteSecret(Store, ArtworkKeyValue, value);
    }

    // ----- Reading the key once -----

    // The values read by ReadOnce on this thread, while its scope is open. Thread static, so a scan on the
    // thread pool never sees the UI thread's copy, which may be a moment out of date by then.
    [ThreadStatic]
    private static Dictionary<string, object?>? _snapshot;

    /// <summary>
    /// Reads every value of the key at once, and has the properties answer from that copy until the scope
    /// is disposed, on this thread only. The library reads about fifteen settings each time it comes to the
    /// front, which was the key opened and closed fifteen times; with this it is opened once. Writes made
    /// meanwhile go to the registry and to the copy alike. A scope inside another is a no-op.
    /// </summary>
    public static IDisposable ReadOnce()
    {
        if (_snapshot is not null)
        {
            return Scope.Nested;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Program.SettingsKey);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                values[name] = key!.GetValue(name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Each setting falls back to its default, as a failed read of one does.
        }

        _snapshot = values;
        return Scope.Outer;
    }

    private sealed class Scope(bool outer) : IDisposable
    {
        public static readonly Scope Outer = new(true);
        public static readonly Scope Nested = new(false);

        public void Dispose()
        {
            if (outer)
            {
                _snapshot = null;
            }
        }
    }

    // ----- The API keys -----

    // The keys are kept encrypted for this Windows account under the same name with this after it, as
    // binary. They used to be kept as plain text under the name alone, which any program, backup or
    // registry export could read; one found there is moved over the first time it is read.
    private const string ProtectedSuffix = "Protected";

    // Ties the encrypted keys to this use, so another program's DPAPI blob is not mistaken for one of ours.
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("HandheldOptimiser.Library.ApiKey");

    /// <summary>Where the API keys are read from and written to: the registry, or a dictionary in the tests.</summary>
    internal interface ISecretStore
    {
        object? Get(string name);

        /// <returns>Whether the value was saved.</returns>
        bool SetBinary(string name, byte[] value);

        void Delete(string name);
    }

    private static readonly ISecretStore Store = new RegistryStore();

    private sealed class RegistryStore : ISecretStore
    {
        public object? Get(string name) => ReadRaw(name);
        public bool SetBinary(string name, byte[] value) => WriteValue(name, value, RegistryValueKind.Binary);
        public void Delete(string name) => WriteValue(name, null, RegistryValueKind.String);
    }

    /// <summary>
    /// An API key, decrypted, or null when none is set or it cannot be decrypted (the profile was copied
    /// to another account, say). A plain text key from before is encrypted, saved and its plain copy deleted.
    /// </summary>
    internal static string? ReadSecret(ISecretStore store, string name)
    {
        var protectedName = name + ProtectedSuffix;
        if (store.Get(protectedName) is byte[] sealedKey)
        {
            // A plain copy left by a move that could not delete it goes now.
            if (store.Get(name) is not null)
            {
                store.Delete(name);
            }

            return Unprotect(sealedKey)?.Trim() is { Length: > 0 } key ? key : null;
        }

        if (store.Get(name) is not string plain || plain.Trim() is not { Length: > 0 } found)
        {
            return null;
        }

        // Only deleted once the encrypted copy is safely saved, so a failed move loses nothing.
        if (Protect(found) is { } sealedFound && store.SetBinary(protectedName, sealedFound))
        {
            store.Delete(name);
            Program.Log($"Moved the {name} setting to encrypted storage");
        }

        return found;
    }

    /// <summary>Saves an API key encrypted, or with null or blank removes it, plain copy and all.</summary>
    internal static void WriteSecret(ISecretStore store, string name, string? value)
    {
        var protectedName = name + ProtectedSuffix;
        if (value?.Trim() is not { Length: > 0 } key)
        {
            store.Delete(protectedName);
            store.Delete(name);
            return;
        }

        if (Protect(key) is { } sealedKey && store.SetBinary(protectedName, sealedKey))
        {
            store.Delete(name);
        }
        else
        {
            // Never written as plain text instead: the player is asked for it again next time.
            Program.Log($"Could not save the {name} setting encrypted");
        }
    }

    /// <summary>The text encrypted for this Windows account, or null when Windows will not.</summary>
    internal static byte[]? Protect(string text)
    {
        try
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(text), SecretEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            Program.Log($"Could not encrypt a setting: {ex.Message}");
            return null;
        }
    }

    /// <summary>Text from <see cref="Protect"/>, or null when it was not encrypted by this account.</summary>
    internal static string? Unprotect(byte[] sealedText)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedText, SecretEntropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            Program.Log($"Could not decrypt a setting: {ex.Message}");
            return null;
        }
    }

    // ----- The registry -----

    private static string? Read(string name) => ReadRaw(name) as string;

    private static object? ReadRaw(string name)
    {
        if (_snapshot is { } snapshot)
        {
            return snapshot.GetValueOrDefault(name);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Program.SettingsKey);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void Write(string name, string? value) => WriteValue(name, value, RegistryValueKind.String);

    /// <summary>Saves a value, or with null or an empty string deletes it.</summary>
    /// <returns>Whether it was saved.</returns>
    private static bool WriteValue(string name, object? value, RegistryValueKind kind)
    {
        var delete = value is null || value is string { Length: 0 };

        // The copy read once is kept in step, so a read later in the same scope sees the change.
        if (_snapshot is { } snapshot)
        {
            if (delete)
            {
                snapshot.Remove(name);
            }
            else
            {
                snapshot[name] = value;
            }
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Program.SettingsKey);
            if (delete)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(name, value!, kind);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The setting still applies until the library closes; it is just not remembered.
            Program.Log($"Could not save the {name} setting: {ex.Message}");
            return false;
        }
    }
}
