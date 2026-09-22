using System.Runtime.InteropServices;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Desktop responsiveness and touch behaviour. Cosmetic, fully reversible, and none of it affects
/// in-game rendering, only the desktop compositor and shell.
/// </summary>
public static class InterfaceTweaks
{
    private const string VisualEffectsId = "ux.visualeffects";

    public static IReadOnlyList<Tweak> All =>
    [
        MenuShowDelay,
        EdgeSwipe,
        VisualEffects
    ];

    public static Tweak MenuShowDelay => new()
    {
        Id = "ux.menushowdelay",
        Name = "Instant menus (zero menu show delay)",
        Description =
            "Removes the 400 ms pause before cascading and context submenus open. Menus appear the moment " +
            "your finger or cursor reaches them.",
        Category = TweakCategory.Interface,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            // Stored as a string, not a DWORD, in every Windows version.
            new(RegistryRoot.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String)
        ]
    };

    public static Tweak EdgeSwipe => new()
    {
        Id = "ux.edgeswipe",
        Name = "Disable edge swipe gestures",
        Description =
            "Stops swipes in from the screen edges opening Widgets, Notifications or Task View, which is " +
            "easy to trigger by accident while gripping the Ally during touch-heavy games.",
        Category = TweakCategory.Interface,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Notifications and Widgets have to be opened from the taskbar or Armoury Crate instead of by " +
            "swiping. This is a machine-wide policy, so it applies to every user account.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI", "AllowEdgeSwipe", 0, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Registry values cover transparency and the taskbar/minimise animations. The general "Animation
    /// effects" switch has no single registry value (it is a bit inside UserPreferencesMask), so it is set
    /// through SystemParametersInfo, which updates that mask correctly and applies it immediately.
    /// </summary>
    public static Tweak VisualEffects => new()
    {
        Id = VisualEffectsId,
        Name = "Disable transparency & window animations",
        Description =
            "Turns off acrylic transparency and window, taskbar and minimise animations. Less work for the " +
            "desktop compositor on the iGPU, and the desktop feels snappier. Games are unaffected.",
        Category = TweakCategory.Interface,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        IncludeInOneClick = false,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarAnimations", 0, RegistryValueKind.DWord),
            new(RegistryRoot.CurrentUser, @"Control Panel\Desktop\WindowMetrics",
                "MinAnimate", "0", RegistryValueKind.String),
            // 3 = "Custom" in Performance Options, so that dialog reflects these choices rather than
            // reapplying "Let Windows choose" the next time it is opened.
            new(RegistryRoot.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                "VisualFXSetting", 3, RegistryValueKind.DWord)
        ],
        ScriptDetect = (_, _) =>
        {
            var enabled = Spi.GetClientAreaAnimation();
            return Task.FromResult(enabled switch
            {
                null => TweakState.Unknown,
                true => TweakState.NotApplied,
                false => TweakState.Applied
            });
        },
        ScriptApply = (ctx, journal, _) =>
        {
            var enabled = Spi.GetClientAreaAnimation();

            if (enabled is null)
            {
                ctx.Log.Error("Could not read the animation effects setting");
                return Task.FromResult(TweakResult.Fail(VisualEffectsId, "Could not read animation effects setting."));
            }

            if (enabled == false)
            {
                ctx.Log.Trace("    Animation effects already off");
                return Task.FromResult(TweakResult.NoChange(VisualEffectsId));
            }

            journal.CapturedState["spi.clientareaanimation"] = "1";

            ctx.Log.Command("SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, off)");
            if (!Spi.SetClientAreaAnimation(false))
            {
                journal.CapturedState.Remove("spi.clientareaanimation");
                ctx.Log.Error($"FAILED to turn off animation effects: Win32 error {Marshal.GetLastWin32Error()}");
                return Task.FromResult(TweakResult.Fail(VisualEffectsId, "Could not turn off animation effects."));
            }

            ctx.Log.Success("SET animation effects = off (was on)");
            return Task.FromResult(TweakResult.Ok(VisualEffectsId));
        },
        ScriptRevert = (ctx, journal, _) =>
        {
            if (!journal.CapturedState.TryGetValue("spi.clientareaanimation", out var original) || original != "1")
            {
                return Task.FromResult(TweakResult.NoChange(VisualEffectsId));
            }

            ctx.Log.Command("SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, on)");
            if (!Spi.SetClientAreaAnimation(true))
            {
                ctx.Log.Error($"FAILED to restore animation effects: Win32 error {Marshal.GetLastWin32Error()}");
                return Task.FromResult(TweakResult.Fail(VisualEffectsId, "Could not restore animation effects."));
            }

            ctx.Log.Success("RESTORE animation effects = on");
            return Task.FromResult(TweakResult.Ok(VisualEffectsId));
        }
    };

    private static class Spi
    {
        private const uint SpiGetClientAreaAnimation = 0x1042;
        private const uint SpiSetClientAreaAnimation = 0x1043;
        private const uint SpifUpdateIniFileAndBroadcast = 0x1 | 0x2;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoGet(uint action, uint param, out int value, uint winIni);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);

        public static bool? GetClientAreaAnimation() =>
            SystemParametersInfoGet(SpiGetClientAreaAnimation, 0, out var value, 0) ? value != 0 : null;

        public static bool SetClientAreaAnimation(bool enabled) =>
            SystemParametersInfoSet(SpiSetClientAreaAnimation, 0, enabled ? 1 : 0, SpifUpdateIniFileAndBroadcast);
    }
}
