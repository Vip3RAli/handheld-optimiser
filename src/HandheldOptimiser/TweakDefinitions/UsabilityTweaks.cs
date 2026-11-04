using System.Runtime.InteropServices;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Stops Windows interrupting a game with prompts or background throttling. The accessibility Flags
/// values are bitmasks stored as strings; 506 and 122 are the defaults (510 and 126) with the
/// "hotkey active" bit cleared.
/// </summary>
public static class UsabilityTweaks
{
    public static IReadOnlyList<Tweak> All =>
    [
        StickyKeys,
        FilterKeys,
        PowerThrottling,
        MouseAcceleration
    ];

    public static Tweak MouseAcceleration => new()
    {
        Id = "usability.mouseacceleration",
        Name = "Turn off pointer acceleration for thumbsticks",
        Description =
            "Turns off Enhanced Pointer Precision so analog thumbsticks and trackpads move the mouse " +
            "cursor at a predictable, 1:1 linear speed.",
        Category = TweakCategory.TouchAndDesktop,
        Risk = RiskLevel.Safe,
        RequiresReboot = false,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String),
            new(RegistryRoot.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String),
            new(RegistryRoot.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String)
        ],

        // The registry values above are only read at sign-in. Telling Windows through SystemParametersInfo
        // makes the change live at once, and undo tells it the values it had before.
        ScriptApply = (ctx, journal, _) =>
        {
            if (Mouse.Get() is not { } was)
            {
                return Task.FromResult(TweakResult.Fail(MouseAccelerationId, "Could not read the pointer settings."));
            }

            if (was is [0, 0, 0])
            {
                return Task.FromResult(TweakResult.NoChange(MouseAccelerationId));
            }

            journal.CapturedState["spi.mouse"] = string.Join(",", was);
            ctx.Log.Command("SystemParametersInfo(SPI_SETMOUSE, 0 0 0)");
            return Task.FromResult(Mouse.Set([0, 0, 0])
                ? TweakResult.Ok(MouseAccelerationId)
                : TweakResult.Fail(MouseAccelerationId, $"Could not turn off pointer acceleration (Win32 error {Marshal.GetLastWin32Error()})."));
        },
        ScriptRevert = (ctx, journal, _) =>
        {
            if (!journal.CapturedState.TryGetValue("spi.mouse", out var saved)
                || saved.Split(',') is not [var a, var b, var c]
                || !int.TryParse(a, out var speed) || !int.TryParse(b, out var first) || !int.TryParse(c, out var second))
            {
                return Task.FromResult(TweakResult.NoChange(MouseAccelerationId));
            }

            ctx.Log.Command($"SystemParametersInfo(SPI_SETMOUSE, {first} {second} {speed})");
            return Task.FromResult(Mouse.Set([speed, first, second])
                ? TweakResult.Ok(MouseAccelerationId)
                : TweakResult.Fail(MouseAccelerationId, $"Could not restore pointer acceleration (Win32 error {Marshal.GetLastWin32Error()})."));
        }
    };

    private const string MouseAccelerationId = "usability.mouseacceleration";

    /// <summary>
    /// The pointer's acceleration through SystemParametersInfo. Windows holds it as three numbers in the
    /// order threshold 1, threshold 2, speed; here they are passed as speed, threshold 1, threshold 2,
    /// the order of the journal and of the registry values.
    /// </summary>
    private static class Mouse
    {
        private const uint SpiGetMouse = 0x0003;
        private const uint SpiSetMouse = 0x0004;
        private const uint SpifUpdateIniFileAndBroadcast = 0x1 | 0x2;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint winIni);

        /// <summary>Speed, threshold 1 and threshold 2, or null if Windows would not say.</summary>
        public static int[]? Get()
        {
            var values = new int[3];
            return SystemParametersInfo(SpiGetMouse, 0, values, 0) ? [values[2], values[0], values[1]] : null;
        }

        public static bool Set(int[] speedFirstSecond) =>
            SystemParametersInfo(SpiSetMouse, 0, [speedFirstSecond[1], speedFirstSecond[2], speedFirstSecond[0]], SpifUpdateIniFileAndBroadcast);
    }

    public static Tweak StickyKeys => new()
    {
        Id = "usability.stickykeys",
        Name = "Turn off the Sticky Keys shortcut",
        Description =
            "Stops pressing Shift five times from opening the Sticky Keys prompt, which can drop you out " +
            "of a game.",
        Category = TweakCategory.TouchAndDesktop,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", RegistryValueKind.String)
        ]
    };

    public static Tweak FilterKeys => new()
    {
        Id = "usability.filterkeys",
        Name = "Turn off the Filter Keys shortcut",
        Description =
            "Stops holding right Shift for eight seconds from turning on Filter Keys, which makes the " +
            "keyboard ignore quick presses.",
        Category = TweakCategory.TouchAndDesktop,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String)
        ]
    };

    public static Tweak PowerThrottling => new()
    {
        Id = "usability.powerthrottling",
        Name = "Turn off power throttling",
        Description =
            "Stops Windows slowing down background apps, so overlays and launchers stay at full speed " +
            "while you play.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Background apps run at full power, which shortens battery life. Best left off if you mostly " +
            "play on battery.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff", 1, RegistryValueKind.DWord)
        ]
    };
}
