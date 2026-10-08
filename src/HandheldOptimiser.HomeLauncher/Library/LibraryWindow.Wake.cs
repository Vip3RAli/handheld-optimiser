using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Back to your game after sleep: when the device wakes with a game running and the library in front, as
/// after choosing Sleep in the power menu, the game is put back in front.
///
/// A Modern Standby device such as the Ally does not tell programs it is going to sleep the old way; its
/// screen turns off instead. So the screen going off and on again counts as sleep and wake too.
/// </summary>
public partial class LibraryWindow
{
    // Unlocking can take a while, so the game is put back if the library comes to the front within this.
    private static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(2);

    // Windows puts the lock screen up, or the last program back in front, just after waking.
    private static readonly TimeSpan SettleAfterWake = TimeSpan.FromSeconds(2);

    private HwndSource? _hwndSource;
    private nint _displayNotification;
    private bool _displayOff;

    // When the device last woke, while it still counts as just woken. Null once the game is back in front,
    // or when the player pressed the home button to come to the library.
    private DateTime? _wokeAt;
    private bool _resuming;

    /// <summary>Whether the device woke a moment ago and the game is still to be put back in front.</summary>
    private bool JustWoke => _wokeAt is { } woke && DateTime.UtcNow - woke <= ResumeWithin;

    private void WatchWake()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(OnWindowMessage);
        _displayNotification = Native.RegisterPowerSettingNotification(handle, Native.ConsoleDisplayState, 0);
    }

    private void StopWatchingWake()
    {
        _hwndSource?.RemoveHook(OnWindowMessage);
        if (_displayNotification != 0)
        {
            Native.UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = 0;
        }
    }

    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.WmPowerBroadcast)
        {
            return 0;
        }

        switch ((int)wParam)
        {
            case Native.PbtApmResumeAutomatic or Native.PbtApmResumeSuspend:
                OnWake();
                break;

            case Native.PbtPowerSettingChange when lParam != 0:
                var setting = Marshal.PtrToStructure<Native.PowerBroadcastSetting>(lParam);
                if (setting.PowerSetting == Native.ConsoleDisplayState && setting.DataLength >= sizeof(uint))
                {
                    // 0 is off, 1 is on and 2 is dimmed, which is still on.
                    if (setting.Data == 0)
                    {
                        _displayOff = true;
                    }
                    else if (setting.Data == 1 && _displayOff)
                    {
                        _displayOff = false;
                        OnWake();
                    }
                }

                break;
        }

        return 0;
    }

    private async void OnWake()
    {
        if (!LibrarySettings.ResumeAfterSleep || _session is null)
        {
            return;
        }

        _wokeAt = DateTime.UtcNow;
        await Task.Delay(SettleAfterWake);
        await ResumeGameAsync();
    }

    /// <summary>
    /// Puts the game being played back in front, if the device has just woken and the library is in front.
    /// When the library is not in front, the game already is, or the lock screen is: this runs again
    /// when the library is next activated.
    /// </summary>
    private async Task ResumeGameAsync()
    {
        if (_wokeAt is not { } woke || DateTime.UtcNow - woke > ResumeWithin || _resuming)
        {
            return;
        }

        if (!IsActive || _session is not { StartedAt: not null } session)
        {
            return;
        }

        // One go only: if the player comes back to the library after this, that is where they want to be.
        _wokeAt = null;
        _resuming = true;
        try
        {
            // Resumed first, if Quick Resume paused it.
            if (await ReturnToGameAsync(session))
            {
                Program.Log($"Back to {session.Game.Key} after waking up");
            }
        }
        finally
        {
            _resuming = false;
        }
    }

    // ----- Settings: While playing -----

    private void OnToggleResume(object sender, RoutedEventArgs e)
    {
        LibrarySettings.ResumeAfterSleep = !LibrarySettings.ResumeAfterSleep;
        RefreshResumeSetting();
    }

    private void RefreshResumeSetting() =>
        ResumeSetting.Tag = LibrarySettings.ResumeAfterSleep
            ? "On: waking up goes back to the game you were playing"
            : "Off: waking up stays where you left off";
}
