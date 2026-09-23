using Microsoft.Win32;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Windows 11 full screen experience ("Xbox mode"): which app it opens as home, and whether the device
/// signs straight into it.
/// </summary>
public static class FullScreenTweaks
{
    private const string HomeAppId = "fse.homeapp";
    private const string RegisteredByUs = "package.registered";

    private static readonly RegistryValueSpec HomeAppSetting = new(
        RegistryRoot.CurrentUser,
        HomeAppRegistration.GamingConfigurationKey,
        HomeAppRegistration.HomeAppValue,
        HomeAppRegistration.AppUserModelId,
        RegistryValueKind.String);

    public static IReadOnlyList<Tweak> All =>
    [
        UseAsHomeApp,
        StartInFullScreen
    ];

    /// <summary>
    /// Registration and the home app switch happen in the script rather than as plain RegistryValues so
    /// the order is guaranteed: the home app is only pointed at us once the package exists. The previous
    /// home app goes into the journal through the registry snapshot, so revert puts it back.
    /// </summary>
    public static Tweak UseAsHomeApp => new()
    {
        Id = HomeAppId,
        Name = "Use Handheld Optimiser as the full screen home app",
        Description =
            "Full screen mode opens the app you choose above (Steam Big Picture, Armoury Crate SE or your own) " +
            "instead of the Xbox app, both at sign-in and whenever you press the home button.",
        Category = TweakCategory.FullScreen,
        Risk = RiskLevel.Moderate,
        IncludeInOneClick = false,
        ScriptRegistryValues = [HomeAppSetting],
        ScriptDetect = (ctx, ct) =>
        {
            if (!HomeAppRegistration.IsFullScreenExperienceAvailable())
            {
                return Task.FromResult(TweakState.Unknown);
            }

            var selected = HomeAppRegistration.IsCurrentHomeApp();

            // An older registration from a previous version counts as partial, so applying refreshes it.
            return Task.FromResult(HomeAppRegistration.IsRegistered() && selected ? TweakState.Applied
                : !HomeAppRegistration.IsAnyVersionRegistered() && !selected ? TweakState.NotApplied
                : TweakState.Partial);
        },
        ScriptApply = async (ctx, journal, ct) =>
        {
            if (!HomeAppRegistration.IsFullScreenExperienceAvailable())
            {
                return TweakResult.Fail(HomeAppId,
                    "The full screen experience is not available on this version of Windows.");
            }

            if (!HomeAppRegistration.IsRegistered())
            {
                if (!await HomeAppRegistration.RegisterAsync(ctx, ct))
                {
                    return TweakResult.Fail(HomeAppId, "Could not register the home app. See log.");
                }

                journal.CapturedState[RegisteredByUs] = "1";
            }

            if (!ctx.Registry.ValueMatches(HomeAppSetting))
            {
                var snapshot = ctx.Registry.WriteValue(HomeAppSetting);
                if (snapshot is null)
                {
                    return TweakResult.Fail(HomeAppId, "Registered, but could not set it as the home app. See log.");
                }

                journal.RegistrySnapshots.Add(snapshot);
            }

            return TweakResult.Ok(HomeAppId, "Handheld Optimiser is now the full screen home app.");
        },
        // Runs before the journalled registry snapshot is restored, so the previous home app comes back
        // after our entry is gone.
        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.ContainsKey(RegisteredByUs))
            {
                return TweakResult.NoChange(HomeAppId);
            }

            return await HomeAppRegistration.UnregisterAsync(ctx, ct)
                ? TweakResult.Ok(HomeAppId, "Removed from the home app list.")
                : TweakResult.Fail(HomeAppId, "Could not remove the home app registration. See log.");
        }
    };

    public static Tweak StartInFullScreen => new()
    {
        Id = "fse.startup",
        Name = "Enter full screen mode at sign-in",
        Description =
            "Signs straight into full screen mode with your home app open, like a console, instead of the " +
            "Windows desktop. You can still switch to the desktop from the Game Bar.",
        Category = TweakCategory.FullScreen,
        Risk = RiskLevel.Safe,
        IncludeInOneClick = false,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, HomeAppRegistration.GamingConfigurationKey,
                HomeAppRegistration.StartupValue, 1, RegistryValueKind.DWord)
        ]
    };
}
