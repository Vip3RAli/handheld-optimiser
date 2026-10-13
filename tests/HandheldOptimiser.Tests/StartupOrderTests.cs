using HandheldOptimiser.ViewModels;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// What the app does as it opens: check for a newer version of itself, and read everything it shows.
/// Opened by the library's Update now, it must not start reading before the update is settled, since
/// a Store check cut off by the app closing leaves updates paused. But if the app then stays open, it
/// has to read after all, or every card on the Dashboard says "Checking…" for good.
/// </summary>
public sealed class StartupOrderTests
{
    [Fact]
    public async Task An_ordinary_start_scans_without_waiting_for_the_update_check()
    {
        var check = new TaskCompletionSource<bool>();
        var scanned = false;

        var run = StartupOrder.RunAsync(installUpdate: false, () => check.Task, () =>
        {
            scanned = true;
            return Task.CompletedTask;
        });

        Assert.True(scanned);

        check.SetResult(false);
        await run;
    }

    [Fact]
    public async Task Opened_to_install_an_update_it_scans_once_the_app_is_staying_open()
    {
        var check = new TaskCompletionSource<bool>();
        var scanned = false;

        var run = StartupOrder.RunAsync(installUpdate: true, () => check.Task, () =>
        {
            scanned = true;
            return Task.CompletedTask;
        });

        Assert.False(scanned);

        // Nothing was installed (offline, already up to date, refused), so the app is not closing.
        check.SetResult(false);
        await run;

        Assert.True(scanned);
    }

    [Fact]
    public async Task Closing_to_install_the_update_it_never_scans()
    {
        var scanned = false;

        await StartupOrder.RunAsync(installUpdate: true, () => Task.FromResult(true), () =>
        {
            scanned = true;
            return Task.CompletedTask;
        });

        Assert.False(scanned);
    }
}
