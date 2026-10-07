using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// How Windows schedules the GPU and prioritises the foreground game.
/// </summary>
public static class GraphicsTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        GameMode,
        WindowedGames,
        HardwareScheduling
    ];

    private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GlobalSettingsValue = "DirectXUserGlobalSettings";
    private const string WindowedSetting = "SwapEffectUpgradeEnable";

    private const string PreviousWindowedSetting = "windowed.previous";
    private const string GlobalSettingsExisted = "windowed.valueexisted";

    /// <summary>Optimizations for windowed games arrived in Windows 11 build 22557; older builds ignore the setting.</summary>
    private const int FirstWindowedGamesBuild = 22557;

    private static bool HasWindowedGamesSetting => Environment.OSVersion.Version.Build >= FirstWindowedGamesBuild;

    private static RegistryValueSpec GlobalSettings(string value) =>
        new(RegistryRoot.CurrentUser, GpuPreferencesKey, GlobalSettingsValue, value, RegistryValueKind.String);

    private static IEnumerable<string> SettingEntries(string? raw) =>
        (raw ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsSetting(string entry, string name) =>
        entry.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSetting(string? raw, string name) =>
        SettingEntries(raw).FirstOrDefault(e => IsSetting(e, name))?[(name.Length + 1)..];

    /// <summary>The same settings string with one entry set, or taken out when <paramref name="value"/> is null.</summary>
    private static string WithSetting(string? raw, string name, string? value)
    {
        var entries = SettingEntries(raw).Where(e => !IsSetting(e, name)).ToList();
        if (value is not null)
        {
            entries.Add($"{name}={value}");
        }

        return entries.Count == 0 ? string.Empty : string.Join(';', entries) + ";";
    }

    /// <summary>
    /// The Settings switch "Optimizations for windowed games". Windows keeps it as one "Name=Value;" entry
    /// in a string shared with the Auto HDR, variable refresh rate and preferred GPU choices, so apply and
    /// revert change that one entry and leave the rest of the string as they find it. A plain registry
    /// snapshot would put the whole string back and undo whatever else the user had changed since.
    /// </summary>
    public static Tweak WindowedGames => new()
    {
        Id = "graphics.windowedgames",
        Name = "Optimise windowed and borderless games",
        Description =
            "Gives DirectX 10 and 11 games in a window or borderless the same low latency as exclusive " +
            "full screen, plus Auto HDR and variable refresh rate. Applies the next time a game starts.",
        Category = TweakCategory.Graphics,
        Risk = RiskLevel.Safe,
        ScriptRegistryValues = [GlobalSettings($"{WindowedSetting}=1;")],

        ScriptDetect = (ctx, ct) =>
        {
            if (!HasWindowedGamesSetting)
            {
                return Task.FromResult(TweakState.Unknown);
            }

            var raw = ctx.Registry.ReadValue(RegistryRoot.CurrentUser, GpuPreferencesKey, GlobalSettingsValue) as string;
            return Task.FromResult(ReadSetting(raw, WindowedSetting) == "1" ? TweakState.Applied : TweakState.NotApplied);
        },

        ScriptApply = (ctx, journal, ct) =>
        {
            if (!HasWindowedGamesSetting)
            {
                return Task.FromResult(TweakResult.Fail("graphics.windowedgames",
                    "Optimisations for windowed games need Windows 11 22H2 or later."));
            }

            var raw = ctx.Registry.ReadValue(RegistryRoot.CurrentUser, GpuPreferencesKey, GlobalSettingsValue) as string;
            var previous = ReadSetting(raw, WindowedSetting);
            if (previous == "1")
            {
                return Task.FromResult(TweakResult.NoChange("graphics.windowedgames"));
            }

            journal.CapturedState[PreviousWindowedSetting] = previous ?? string.Empty;
            if (raw is not null)
            {
                journal.CapturedState[GlobalSettingsExisted] = "1";
            }

            return Task.FromResult(ctx.Registry.WriteValue(GlobalSettings(WithSetting(raw, WindowedSetting, "1"))) is null
                ? TweakResult.Fail("graphics.windowedgames", "Could not switch on optimisations for windowed games. See log.")
                : TweakResult.Ok("graphics.windowedgames", "Optimisations for windowed games are on."));
        },

        ScriptRevert = (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue(PreviousWindowedSetting, out var previous))
            {
                return Task.FromResult(TweakResult.NoChange("graphics.windowedgames"));
            }

            // Only "0" is ever put back; anything else in the undo data means the entry was not there.
            var raw = ctx.Registry.ReadValue(RegistryRoot.CurrentUser, GpuPreferencesKey, GlobalSettingsValue) as string;
            var restored = WithSetting(raw, WindowedSetting, previous == "0" ? "0" : null);

            // Nothing left and no value to begin with: take the value away again rather than leave it empty.
            var ok = restored.Length == 0 && !journal.CapturedState.ContainsKey(GlobalSettingsExisted)
                ? ctx.Registry.RestoreSnapshot(new RegistryValueSnapshot
                {
                    Root = RegistryRoot.CurrentUser,
                    SubKey = GpuPreferencesKey,
                    ValueName = GlobalSettingsValue,
                    KeyExisted = true,
                    ValueExisted = false
                })
                : ctx.Registry.WriteValue(GlobalSettings(restored)) is not null;

            return Task.FromResult(ok
                ? TweakResult.Ok("graphics.windowedgames", "Optimisations for windowed games are back as they were.")
                : TweakResult.Fail("graphics.windowedgames", "Could not put the windowed games setting back. See log."));
        }
    };

    /// <summary>
    /// AutoGameModeEnabled is the Settings switch itself; AllowAutoGameMode is the older companion value.
    /// Both are set so the result is the same whichever one a given build reads.
    /// </summary>
    public static Tweak GameMode => new()
    {
        Id = "graphics.gamemode",
        Name = "Force Game Mode on",
        Description =
            "Keeps Game Mode on, so Windows prioritises the game and holds back driver installs and " +
            "restart prompts while you play.",
        Category = TweakCategory.Graphics,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord)
        ]
    };

    public static Tweak HardwareScheduling => new()
    {
        Id = "graphics.hags",
        Name = "Enable hardware-accelerated GPU scheduling",
        Description =
            "Lets the GPU manage its own memory and work queue. Lowers input latency slightly, and some " +
            "games need it for frame generation.",
        Category = TweakCategory.Graphics,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Results vary by game and driver. If you notice new stutter after restarting, switch this back off.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord)
        ]
    };
}
