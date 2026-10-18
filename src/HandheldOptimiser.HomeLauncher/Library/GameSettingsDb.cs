using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>One setting to change inside a game, as its own menu names it.</summary>
internal sealed record GameSetting(string Name, string Value);

/// <summary>Where a game's recommended settings came from.</summary>
internal sealed record SettingsSource(string Name, string Url);

/// <summary>
/// The device half of a recommendation, which the game's profile can take as it is. Every part is
/// optional; a missing one leaves that row of the profile as it was.
/// </summary>
internal sealed record DeviceSettings(string? Power, string? Resolution, int? Refresh, int? Fps, bool? CloseApps);

/// <summary>Recommended Ally settings for one game, from data/game-settings.json in the repository.</summary>
/// <param name="Keys">The library keys it is for, such as "steam:292030".</param>
/// <param name="Titles">Names it can also be matched by, for a store whose key is not listed.</param>
/// <param name="Confidence">"guide" when it comes from a published Ally guide, "starting-point" otherwise.</param>
internal sealed record GameRecommendation(
    string Title,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Titles,
    string Target,
    DeviceSettings? Device,
    IReadOnlyList<GameSetting> Settings,
    string? Note,
    SettingsSource? Source,
    string? Checked,
    string? Confidence)
{
    public bool FromGuide => string.Equals(Confidence, "guide", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's profile with the device half laid over it. Rows the recommendation leaves out keep
    /// what the player chose; brightness is never touched.
    /// </summary>
    public GameProfile ApplyTo(GameProfile profile)
    {
        if (Device is not { } device)
        {
            return profile;
        }

        var power = device.Power is { } name && Enum.TryParse<PowerMode>(name, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode : profile.PowerMode;
        var size = device.Resolution is { } text ? Resolution.Parse(text) ?? profile.Resolution : profile.Resolution;
        var hertz = device.Refresh is > 1 ? device.Refresh : profile.RefreshRate;
        var fps = device.Fps is { } rate && AutoTdp.FrameRates.Contains(rate) ? rate : profile.AutoTdpFps;

        return profile with
        {
            PowerMode = power,
            Resolution = size,
            RefreshRate = hertz,
            CloseApps = device.CloseApps ?? profile.CloseApps,
            AutoTdpFps = fps
        };
    }

    /// <summary>The device half in one line, the way the game profile describes itself.</summary>
    public string DescribeDevice() =>
        Device is null ? "No device settings" : ApplyTo(GameProfile.None).Describe();
}

/// <summary>The whole list, with the date it was last changed.</summary>
internal sealed record GameSettingsList(string Updated, IReadOnlyList<GameRecommendation> Games)
{
    public static readonly GameSettingsList Empty = new("", []);

    /// <summary>The recommendation for a game, by its key first and then by its name.</summary>
    public GameRecommendation? For(Game game)
    {
        if (game.IsApp || game.Store is GameStore.Emulator)
        {
            return null;
        }

        var byKey = Games.FirstOrDefault(g => g.Keys.Contains(game.Key, StringComparer.OrdinalIgnoreCase));
        if (byKey is not null)
        {
            return byKey;
        }

        var title = GameSettingsDb.Normalise(game.Title);
        return title.Length == 0 ? null
            : Games.FirstOrDefault(g => g.Titles.Append(g.Title).Any(t => GameSettingsDb.Normalise(t) == title));
    }
}

/// <summary>
/// Recommended Ally settings for the player's games. A copy is built into the app; a newer one is
/// fetched from the repository at most once a day, so games can be added without a new release. The
/// list is only read: nothing is changed until the player picks a game's recommendation.
/// </summary>
internal static class GameSettingsDb
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/Vip3RAli/handheld-optimiser/main/data/game-settings.json";
    private const string ResourceName = "game-settings.json";

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandheldOptimiser", "game-settings.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static GameSettingsList? _current;

    /// <summary>The newest list there is: the downloaded one when it is newer than the built-in one.</summary>
    public static GameSettingsList Current => _current ??= Load();

    /// <summary>Fetches the list from the repository when the saved copy is a day old or missing.</summary>
    /// <returns>True when a newer list was saved, so open menus can show it.</returns>
    public static async Task<bool> RefreshAsync()
    {
        try
        {
            if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < RefreshEvery)
            {
                return false;
            }

            var text = await Http.GetStringAsync(RemoteUrl);
            if (Parse(text) is not { } list)
            {
                Program.Log("The game settings list from GitHub could not be read");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, text);

            var newer = string.CompareOrdinal(list.Updated, Current.Updated) > 0;
            if (newer)
            {
                _current = list;
            }

            return newer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // Offline is fine: the built-in or saved list is still there.
            Program.Log($"Could not fetch the game settings list: {ex.Message}");
            return false;
        }
    }

    private static GameSettingsList Load()
    {
        var builtIn = ReadBuiltIn() ?? GameSettingsList.Empty;
        GameSettingsList? saved = null;
        try
        {
            if (File.Exists(CachePath))
            {
                saved = Parse(File.ReadAllText(CachePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log($"Could not read the saved game settings list: {ex.Message}");
        }

        // An app update can carry a newer list than one downloaded before it.
        return saved is not null && string.CompareOrdinal(saved.Updated, builtIn.Updated) > 0 ? saved : builtIn;
    }

    /// <summary>The list built into the app, or null if it is missing or broken.</summary>
    internal static GameSettingsList? ReadBuiltIn()
    {
        using var stream = typeof(GameSettingsDb).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The list from its JSON, or null when it is not a list at all. Games missing a title are left out.</summary>
    public static GameSettingsList? Parse(string json)
    {
        FileShape? file;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file?.Games is not { } games)
        {
            return null;
        }

        var list = games
            .Where(g => !string.IsNullOrWhiteSpace(g.Title))
            .Select(g => new GameRecommendation(
                g.Title!.Trim(),
                g.Keys ?? [],
                g.Titles ?? [],
                string.IsNullOrWhiteSpace(g.Target) ? "Recommended" : g.Target.Trim(),
                g.Device,
                (g.Settings ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList(),
                g.Note,
                g.Source,
                g.Checked,
                g.Confidence))
            .ToList();

        return new GameSettingsList(file.Updated ?? "", list);
    }

    /// <summary>A name with only its letters and digits, lower case: "Baldur's Gate 3" is "baldursgate3".</summary>
    public static string Normalise(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private sealed class FileShape
    {
        public string? Updated { get; set; }

        public List<GameShape>? Games { get; set; }
    }

    private sealed class GameShape
    {
        public string? Title { get; set; }

        public List<string>? Keys { get; set; }

        public List<string>? Titles { get; set; }

        public string? Target { get; set; }

        public DeviceSettings? Device { get; set; }

        public List<GameSetting>? Settings { get; set; }

        public string? Note { get; set; }

        public SettingsSource? Source { get; set; }

        public string? Checked { get; set; }

        public string? Confidence { get; set; }
    }
}
