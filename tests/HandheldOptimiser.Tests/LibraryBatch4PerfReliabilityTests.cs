using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

public sealed class LibraryBatch4PerfReliabilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ho-batch4-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryBatch4PerfReliabilityTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Backdrops_GradientProducesFrozenBitmapWithPooledBuffers()
    {
        for (var i = 0; i < 4; i++)
        {
            var bmp = Backdrops.Gradient(
                Color.FromRgb(0x20, 0x40, 0x80),
                Color.FromRgb(0x10, 0x18, 0x24),
                640,
                360,
                0.8);
            Assert.True(bmp.IsFrozen);
            Assert.Equal(640, bmp.PixelWidth);
            Assert.Equal(360, bmp.PixelHeight);
        }
    }

    [Fact]
    public void GameSettingsList_For_ResolvesByKeyAndNormalisedTitleWithFirstMatchPrecedence()
    {
        var first = new GameRecommendation(
            Title: "Hades II",
            Keys: ["steam:1145350"],
            Titles: ["Hades 2"],
            Target: "60 fps",
            Device: null,
            Settings: [],
            Note: null,
            Source: null,
            Checked: null,
            Confidence: "guide");
        var second = new GameRecommendation(
            Title: "Hades II",
            Keys: ["epic:hades2"],
            Titles: [],
            Target: "45 fps",
            Device: null,
            Settings: [],
            Note: null,
            Source: null,
            Checked: null,
            Confidence: null);

        var list = new GameSettingsList("2026-11-27", [first, second]);

        Assert.Same(first, list.For(new Game("STEAM:1145350", "Wrong Title", GameStore.Steam, null, null, "x")));
        Assert.Same(second, list.For(new Game("epic:hades2", "Hades II", GameStore.Epic, null, null, "x")));
        Assert.Same(first, list.For(new Game("gog:999", "Hades 2", GameStore.Gog, null, null, "x")));
        Assert.Null(list.For(new Game("app:calc", "Hades II", GameStore.Other, null, null, "x", IsApp: true)));
        Assert.Null(list.For(new Game("emu:snes:1", "Hades II", GameStore.Emulator, null, null, "x")));
    }

    [Fact]
    public void SaveGuard_SkipIfUnchangedSkipsDuplicateSnapshotsUntilFilesChange()
    {
        var saveDir = Path.Combine(_folder, "saves");
        var backupDir = Path.Combine(_folder, "backups");
        Directory.CreateDirectory(saveDir);
        var saveFile = Path.Combine(saveDir, "slot1.sav");
        File.WriteAllText(saveFile, "progress-1");
        var game = new Game("steam:42", "Save Test", GameStore.Steam, null, null, "steam://rungameid/42");

        var first = SaveGuard.CreateSnapshot(game, saveDir, backupDir, skipIfUnchanged: true);
        Assert.NotNull(first);
        Assert.Single(SaveGuard.SnapshotsFor(game, backupDir));

        var second = SaveGuard.CreateSnapshot(game, saveDir, backupDir, skipIfUnchanged: true);
        Assert.NotNull(second);
        Assert.Equal(first!.ZipPath, second!.ZipPath);
        Assert.Single(SaveGuard.SnapshotsFor(game, backupDir));

        File.WriteAllText(saveFile, "progress-2-updated");
        File.SetLastWriteTimeUtc(saveFile, DateTime.UtcNow.AddSeconds(5));
        var third = SaveGuard.CreateSnapshot(game, saveDir, backupDir, skipIfUnchanged: true);
        Assert.NotNull(third);
        Assert.NotEqual(first.ZipPath, third!.ZipPath);
        Assert.Equal(2, SaveGuard.SnapshotsFor(game, backupDir).Count);
    }

    [Fact]
    public void GameSession_SetDisplayOffUpdatesIdleTracking()
    {
        var game = new Game("other:1", "Test", GameStore.Other, null, null, "cmd.exe");
        var session = new GameSession(game);

        typeof(GameSession).GetProperty(nameof(GameSession.StartedAt))!
            .SetValue(session, DateTime.UtcNow.AddSeconds(-10));

        session.SetDisplayOff(true);
        var idleSinceField = typeof(GameSession).GetField("_idleSince", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(idleSinceField.GetValue(session));

        Thread.Sleep(50);
        session.SetDisplayOff(false);
        Assert.Null(idleSinceField.GetValue(session));
        var idleField = typeof(GameSession).GetField("_idle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((TimeSpan)idleField.GetValue(session)! > TimeSpan.Zero);
    }

    [Fact]
    public void WindowsProcessOptimiser_PreservesRadeonSoftwareAndKeepsOptInInstantReplay()
    {
        var defaultTargets = WindowsProcessOptimiser.TargetProcesses(stopAmdRecording: false);
        var optInTargets = WindowsProcessOptimiser.TargetProcesses(stopAmdRecording: true);

        Assert.DoesNotContain("RadeonSoftware", defaultTargets);
        Assert.DoesNotContain("RadeonSoftware", optInTargets);
        Assert.DoesNotContain("AMDRSSrcExt", defaultTargets);
        Assert.Contains("AMDRSSrcExt", optInTargets);
    }

    [Fact]
    public void LibraryXaml_UsesLinearScalingDesiredFrameRateAndNoEmptyTextShadow()
    {
        var windowXaml = File.ReadAllText(Path.Combine(LibraryPath(), "LibraryWindow.xaml"));
        Assert.DoesNotContain("DropShadowEffect", windowXaml);

        var resourcesXaml = File.ReadAllText(Path.Combine(LibraryPath(), "LibraryResources.xaml"));
        Assert.DoesNotContain("BitmapScalingMode=\"HighQuality\"", resourcesXaml);
        Assert.Contains("Timeline.DesiredFrameRate=\"30\"", resourcesXaml);
    }

    private static string LibraryPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "HandheldOptimiser.HomeLauncher", "Library"));
}
