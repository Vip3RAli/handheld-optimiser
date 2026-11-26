using System.IO;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

public class HighPriorityFixesTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AutoTdp_step_up_after_no_gain_expiry_respects_thermal_ceiling()
    {
        var settings = new AutoTdpSettings(TargetFps: 60, MinWatts: 7, MaxWatts: 30, TemperatureLimit: 80);
        var controller = new AutoTdpController(settings, startWatts: 25);

        // First 12 seconds: hot (85 C) drives watts down under the thermal limit.
        for (var s = 0; s < 12; s++)
        {
            controller.Tick(Start.AddSeconds(s), 45, 85);
        }

        var ceilingWatts = controller.Watts;
        Assert.True(ceilingWatts < 25, $"Expected thermal cut below 25 W, got {ceilingWatts} W");
        Assert.Equal(AutoTdpState.TooHot, controller.State);

        // Next 90 seconds: hold just under the limit (79 C, still not cooled by 3 C) while FPS stays
        // flat at 30 so HoldNoGain triggers and then expires, attempting StepUp.
        for (var s = 12; s < 102; s++)
        {
            controller.Tick(Start.AddSeconds(s), 30, 79);
            Assert.True(controller.Watts <= ceilingWatts,
                $"At second {s}, watts ({controller.Watts} W) exceeded thermal ceiling ({ceilingWatts} W)");
        }

        // Now FPS drops slightly then recovers or stays short of target while 79 C: ceiling must hold.
        for (var s = 102; s < 120; s++)
        {
            controller.Tick(Start.AddSeconds(s), s % 2 == 0 ? 40 : 42, 79);
            Assert.True(controller.Watts <= ceilingWatts,
                $"At second {s}, watts ({controller.Watts} W) exceeded thermal ceiling ({ceilingWatts} W)");
        }
    }

    [Theory]
    [InlineData("https://store-images.s-microsoft.com/image/apps.12345.png", true)]
    [InlineData("https://images-eds-ssl.xboxlive.com/image?url=abc", true)]
    [InlineData("https://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg", true)]
    [InlineData("http://store-images.s-microsoft.com/image/apps.12345.png", false)]
    [InlineData("https://evil-microsoft.com/image.png", false)]
    [InlineData("https://example.com/image.png", false)]
    public void Artwork_IsStoreImageHost_allows_microsoft_cdns_over_https_only(string url, bool expected)
    {
        Assert.Equal(expected, Artwork.IsStoreImageHost(new Uri(url)));
    }

    [Fact]
    public void SaveGuard_BeforeRestoreFor_and_RestoreSnapshot_recover_emptied_save_folder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ho-saveguard-{Guid.NewGuid():N}");
        var saveFolder = Path.Combine(tempRoot, "saves");
        var backupsRoot = Path.Combine(tempRoot, "backups");
        Directory.CreateDirectory(saveFolder);

        try
        {
            var game = new Game("steam:12345", "Test Game", GameStore.Steam, null, null, "12345");
            var saveFile = Path.Combine(saveFolder, "slot1.sav");
            File.WriteAllText(saveFile, "v1-original");

            var snapshot = SaveGuard.CreateSnapshot(game, saveFolder, backupsRoot);
            Assert.NotNull(snapshot);

            // Overwrite with corrupted state, then restore snapshot (which writes before-restore.zip + source.txt).
            File.WriteAllText(saveFile, "v2-pre-restore");
            Assert.True(SaveGuard.RestoreSnapshot(snapshot!, saveFolder));
            Assert.Equal("v1-original", File.ReadAllText(saveFile));

            // BeforeRestoreFor must resolve the original saveFolder from source.txt so restoring
            // into a wiped folder works without passing targetFolder.
            var beforeRestore = SaveGuard.BeforeRestoreFor(game, backupsRoot);
            Assert.NotNull(beforeRestore);
            Assert.Equal(saveFolder, beforeRestore!.SourceFolder);

            Directory.Delete(saveFolder, recursive: true);
            Assert.True(SaveGuard.RestoreSnapshot(beforeRestore));
            Assert.Equal("v2-pre-restore", File.ReadAllText(saveFile));
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
    public void NativeInstalls_SetUpdateBuildId_and_Uninstall_handle_missing_or_empty_records_safely()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ho-installs-{Guid.NewGuid():N}");
        var previousFolder = NativeInstalls.Folder;
        NativeInstalls.Folder = Path.Combine(tempRoot, "record");

        try
        {
            var gameFolderA = Path.Combine(tempRoot, "GameA");
            Directory.CreateDirectory(gameFolderA);
            var userSave = Path.Combine(gameFolderA, "user.sav");
            File.WriteAllText(userSave, "keep-me");

            var installA = new NativeInstall(
                GameStore.Gog,
                "game-a",
                "Game A",
                "build-1",
                "1.0",
                gameFolderA,
                "game.exe",
                null,
                null,
                100,
                DateTimeOffset.UtcNow);

            NativeInstalls.Save(installA);

            // Mismatched expectedBuildId is ignored.
            NativeInstalls.SetUpdateBuildId(installA.Key, "wrong-build", "build-2");
            Assert.Null(NativeInstalls.Find(installA.Key)?.UpdateBuildId);

            // Matching expectedBuildId updates cleanly.
            NativeInstalls.SetUpdateBuildId(installA.Key, "build-1", "build-2");
            Assert.Equal("build-2", NativeInstalls.Find(installA.Key)?.UpdateBuildId);

            // Removed install is not re-inserted.
            NativeInstalls.Remove(installA.Key);
            NativeInstalls.SetUpdateBuildId(installA.Key, "build-1", "build-3");
            Assert.Null(NativeInstalls.Find(installA.Key));

            // Empty files.json on Uninstall must NOT recursively delete user files in gameFolderA.
            NativeInstalls.Save(installA);
            NativeInstalls.WriteJson(NativeInstalls.FilesPath(GameStore.Gog, "game-a"), new Dictionary<string, string>());
            var kept = NativeInstalls.Uninstall(installA);
            Assert.True(kept);
            Assert.True(File.Exists(userSave));

            // Subfolder missing on disk must not crash Uninstall and still removes tracked files.
            var gameFolderB = Path.Combine(tempRoot, "GameB");
            Directory.CreateDirectory(gameFolderB);
            var trackedFile = Path.Combine(gameFolderB, "bin", "game.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(trackedFile)!);
            File.WriteAllText(trackedFile, "exe");

            var installB = installA with { Id = "game-b", Title = "Game B", Folder = gameFolderB };
            NativeInstalls.Save(installB);
            NativeInstalls.WriteJson(
                NativeInstalls.FilesPath(GameStore.Gog, "game-b"),
                new Dictionary<string, string>
                {
                    ["bin/game.exe"] = "1",
                    ["missing-dir/old.pak"] = "2",
                });

            var keptB = NativeInstalls.Uninstall(installB);
            Assert.False(keptB);
            Assert.False(File.Exists(trackedFile));
            Assert.Null(NativeInstalls.Find(installB.Key));
        }
        finally
        {
            NativeInstalls.Folder = previousFolder;
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GameSettingsDb_Parse_skips_null_entries_and_null_fields_safely()
    {
        const string json = """
        {
          "updated": "2026-11-01",
          "games": [
            null,
            { "title": null, "keys": ["steam:1"] },
            { "title": "   ", "keys": ["steam:2"] },
            {
              "title": "Hollow Knight",
              "keys": [null, " ", "steam:367520"],
              "titles": [null, "Hollow Knight"],
              "settings": [
                null,
                { "name": null, "value": "High" },
                { "name": "Particles", "value": null }
              ]
            }
          ]
        }
        """;

        var list = GameSettingsDb.Parse(json);
        Assert.NotNull(list);
        Assert.Single(list!.Games);

        var rec = list.Games[0];
        Assert.Equal("Hollow Knight", rec.Title);
        Assert.Equal(["steam:367520"], rec.Keys);
        Assert.Equal(["Hollow Knight"], rec.Titles);
        Assert.Single(rec.Settings);
        Assert.Equal("Particles", rec.Settings[0].Name);
        Assert.Equal(string.Empty, rec.Settings[0].Value);
    }

    [Fact]
    public void RuntimeService_ParseListRows_parses_localised_and_varied_column_layouts()
    {
        const string germanFiveCols = """
            Name                                    Id                           Version     Verfügbar  Quelle
            --------------------------------------------------------------------------------------------------
            Microsoft Visual C++ 2015-2022 Redist   Microsoft.VCRedist.2015+.x64 14.40.33810 14.42.34433 winget
            """;

        var deRows = RuntimeService.ParseListRows(germanFiveCols, "Microsoft.VCRedist.2015+.x64");
        Assert.Single(deRows);
        Assert.Equal(("14.40.33810", "14.42.34433"), deRows[0]);

        const string spanishFourCols = """
            Nombre                                  Id                           Versión     Origen
            ---------------------------------------------------------------------------------------
            Microsoft Visual C++ 2015-2022 Redist   Microsoft.VCRedist.2015+.x64 14.40.33810 winget
            """;

        var esRows = RuntimeService.ParseListRows(spanishFourCols, "Microsoft.VCRedist.2015+.x64");
        Assert.Single(esRows);
        Assert.Equal(("14.40.33810", null), esRows[0]);

        const string threeCols = """
            名前                                    ID                           バージョン
            ---------------------------------------------------------------------------------
            Microsoft Visual C++ 2015-2022 Redist   Microsoft.VCRedist.2015+.x64 14.42.34433
            """;

        var jaRows = RuntimeService.ParseListRows(threeCols, "Microsoft.VCRedist.2015+.x64");
        Assert.Single(jaRows);
        Assert.Equal(("14.42.34433", null), jaRows[0]);
    }

    [Fact]
    public void GamingTweaks_BuildVirtualisationRevertScript_deletes_absent_bcd_value_and_restores_auto()
    {
        var absentScript = GamingTweaks.BuildVirtualisationRevertScript(new Dictionary<string, string>
        {
            ["bcd.hypervisorlaunchtype"] = "absent",
        });

        Assert.Contains("bcdedit /deletevalue '{current}' hypervisorlaunchtype", absentScript);
        Assert.DoesNotContain("bcdedit /set '{current}' hypervisorlaunchtype", absentScript);

        var autoScript = GamingTweaks.BuildVirtualisationRevertScript(new Dictionary<string, string>
        {
            ["bcd.hypervisorlaunchtype"] = "Auto",
        });

        Assert.Contains("bcdedit /set '{current}' hypervisorlaunchtype Auto", autoScript);
    }

    [Fact]
    public void DiagnosticsReport_Scrub_redacts_session_user_account_alongside_process_user()
    {
        const string raw = "Elevated=HOST\\admin Session=CONTOSO\\player User=player Path=C:\\Users\\player\\AppData";

        var scrubbed = DiagnosticsReport.Scrub(
            raw,
            profileFolder: @"C:\Users\admin",
            userName: "admin",
            machineName: "HOST",
            sessionUserName: @"CONTOSO\player");

        Assert.DoesNotContain("CONTOSO\\player", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("player", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HOST", scrubbed, StringComparison.OrdinalIgnoreCase);
    }
}
