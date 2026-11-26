using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

public class PerformanceAndUxFixesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1056, true)]
    [InlineData(1060, false)]
    [InlineData(1058, false)]
    [InlineData(5, false)]
    [InlineData(-1, false)]
    public void TdpEngineClient_IsStartSuccess_recognises_started_and_already_running_only(int exitCode, bool expected)
    {
        Assert.Equal(expected, TdpEngineClient.IsStartSuccess(exitCode));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(3840, 1600, 1920, 800)]
    [InlineData(2160, 3840, 608, 1080)]
    public void Backdrops_CapSize_bounds_dimensions_to_1080p_preserving_aspect_ratio(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), Backdrops.CapSize(width, height));
    }

    [Theory]
    [InlineData(25000, 17000, 0x0008)]
    [InlineData(-25000, -17000, 0x0004)]
    [InlineData(17000, 25000, 0x0001)]
    [InlineData(17000, -25000, 0x0002)]
    [InlineData(10500, 10500, 0)]
    [InlineData(-1000, 1000, 0)]
    [InlineData(short.MinValue, 0, 0x0004)]
    [InlineData(0, short.MinValue, 0x0002)]
    [InlineData(short.MinValue, short.MinValue, 0x0002)]
    public void GamepadInput_StickToDpad_picks_dominant_axis_and_handles_min_short(short lx, short ly, ushort expected)
    {
        Assert.Equal(expected, GamepadInput.StickToDpad(lx, ly));
    }

    [Fact]
    public void GamePause_FindAntiCheat_detects_deep_xbox_and_expanded_anti_cheats()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ho-anticheat-{Guid.NewGuid():N}");
        try
        {
            // Depth 4 under Content\Game\Binaries\Win64\EasyAntiCheat (Xbox Game Pass layout)
            var deepEac = Path.Combine(tempRoot, "eac", "Content", "Game", "Binaries", "Win64", "EasyAntiCheat");
            Directory.CreateDirectory(deepEac);
            Assert.Equal("Easy Anti-Cheat", GamePause.FindAntiCheat(Path.Combine(tempRoot, "eac")));

            // Expanded signatures
            var cases = new (string Folder, string Relative, bool IsDir, string Expected)[]
            {
                ("ricochet", "randgrid.sys", false, "RICOCHET Anti-Cheat"),
                ("vanguard", "vgc.exe", false, "Riot Vanguard"),
                ("punkbuster", "PnkBstrA.exe", false, "PunkBuster"),
                ("sguard", "SGuard64.exe", false, "ACE SGuard"),
                ("blackcipher", "BlackCipher.aes", false, "Nexon BlackCipher"),
                ("xigncode", "x3.xem", false, "XIGNCODE3"),
                ("pb-dir", "pb", true, string.Empty),
            };

            foreach (var (folder, relative, isDir, expected) in cases)
            {
                var gameDir = Path.Combine(tempRoot, folder);
                var target = Path.Combine(gameDir, relative);
                if (isDir)
                {
                    Directory.CreateDirectory(target);
                    Assert.Null(GamePause.FindAntiCheat(gameDir));
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "");
                    Assert.Equal(expected, GamePause.FindAntiCheat(gameDir));
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void WindowsProcessOptimiser_TargetProcesses_treats_amd_instant_replay_as_opt_in()
    {
        Assert.DoesNotContain("AMDRSSrcExt", WindowsProcessOptimiser.TargetProcesses(stopAmdRecording: false));
        Assert.Contains("AMDRSSrcExt", WindowsProcessOptimiser.TargetProcesses(stopAmdRecording: true));
    }

    [Fact]
    public async Task TweakStateStore_CheckMissingAsync_retries_cancelled_initial_checks_and_skips_in_flight()
    {
        var cancelledTweak = new Tweak
        {
            Id = "cancelled",
            Name = "cancelled",
            Description = "Cancelled initially",
            Category = TweakCategory.Games,
        };
        var answeredTweak = new Tweak
        {
            Id = "answered",
            Name = "answered",
            Description = "Already answered",
            Category = TweakCategory.Games,
        };
        var inFlightTweak = new Tweak
        {
            Id = "inflight",
            Name = "inflight",
            Description = "Still running",
            Category = TweakCategory.Games,
        };

        var calls = new Dictionary<string, int>();
        var inFlightTcs = new TaskCompletionSource<TweakState>();
        using var initialCancel = new CancellationTokenSource();
        initialCancel.Cancel();

        var store = new TweakStateStore(
            [cancelledTweak, answeredTweak, inFlightTweak],
            (tweak, ct) =>
            {
                calls[tweak.Id] = calls.GetValueOrDefault(tweak.Id) + 1;
                if (tweak.Id == "inflight")
                {
                    return inFlightTcs.Task;
                }

                ct.ThrowIfCancellationRequested();
                return Task.FromResult(TweakState.Applied);
            });

        // Initial check for cancelledTweak gets cancelled, answeredTweak completes normally.
        await store.CheckAsync([cancelledTweak], initialCancel.Token);
        await store.CheckAsync([answeredTweak]);
        var pending = store.CheckAsync([inFlightTweak]);

        Assert.Null(store.StateOf(cancelledTweak));
        Assert.Equal(TweakState.Applied, store.StateOf(answeredTweak));

        // CheckMissingAsync should retry cancelledTweak, skip answeredTweak, and skip inFlightTweak.
        await store.CheckMissingAsync([cancelledTweak, answeredTweak, inFlightTweak]);

        Assert.Equal(2, calls["cancelled"]);
        Assert.Equal(1, calls["answered"]);
        Assert.Equal(1, calls["inflight"]);
        Assert.Equal(TweakState.Applied, store.StateOf(cancelledTweak));

        inFlightTcs.SetResult(TweakState.NotApplied);
        await pending;
    }
}
