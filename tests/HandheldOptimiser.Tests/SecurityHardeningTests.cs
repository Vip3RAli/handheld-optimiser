using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using HandheldOptimiser.HomeLauncher.Library;
using HandheldOptimiser.Models;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.Services;
using HandheldOptimiser.TweakDefinitions;
using Xunit;

namespace HandheldOptimiser.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void SanitizeUiAccessEnvironment_strips_loader_vars_and_replaces_user_path()
    {
        var input = new[]
        {
            "=C:=C:\\Users\\player",
            "USERPROFILE=C:\\Users\\player",
            "DOTNET_STARTUP_HOOKS=C:\\Users\\player\\evil.dll",
            "CORECLR_ENABLE_PROFILING=1",
            "COR_PROFILER={00000000-0000-0000-0000-000000000000}",
            "COMPlus_DbgEnableMiniDump=1",
            "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Evil",
            "PATH=C:\\Users\\player\\bin;C:\\Windows\\System32",
            "LOCALAPPDATA=C:\\Users\\player\\AppData\\Local"
        };

        var sanitized = AutoTdpRules.SanitizeUiAccessEnvironment(input, @"C:\Windows\System32;C:\Windows");

        Assert.Equal(
            [
                "LOCALAPPDATA=C:\\Users\\player\\AppData\\Local",
                "PATH=C:\\Windows\\System32;C:\\Windows",
                "USERPROFILE=C:\\Users\\player"
            ],
            sanitized);
    }

    [Fact]
    public void BuildEnvironmentBlock_terminates_with_double_null()
    {
        var block = AutoTdpRules.BuildEnvironmentBlock(["A=1", "B=2"]);
        Assert.Equal("A=1\0B=2\0\0", new string(block));

        var empty = AutoTdpRules.BuildEnvironmentBlock([]);
        Assert.Equal("\0\0", new string(empty));
    }

    [Fact]
    public void CsvSafeName_strips_newlines_control_chars_commas_and_quotes()
    {
        var safe = AutoTdpRules.CsvSafeName(@"C:\Games\My, ""Game""" + "\r\nFAKE,ROW");
        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain(',', safe);
        Assert.DoesNotContain('"', safe);
        Assert.Equal("My   Game ??FAKE ROW", safe);
    }

    [Fact]
    public void ArmouryCrate_BuildRevertScript_rejects_injected_oem_and_invalid_hex()
    {
        var captured = new Dictionary<string, string>
        {
            [ArmouryCrateTweaks.OemWidgetJournalKey] = "Evil.Pkg_1234567890123\u2019; Start-Process calc #",
            [ArmouryCrateTweaks.ScreenManagerJournalKey] = "NOT_VALID_HEX!"
        };

        var script = ArmouryCrateTweaks.BuildRevertScript(captured);

        Assert.DoesNotContain("Start-Process calc", script, StringComparison.Ordinal);
        Assert.Contains(ArmouryCrateTweaks.CommandCenterFamily, script, StringComparison.Ordinal);
        Assert.DoesNotContain("Putting back the startup entry", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DropUntrustedLegacyState_strips_oem_widget_run_flag_and_amd_entries()
    {
        var entry = new TweakJournalEntry
        {
            TweakId = "asus.replace-armoury-crate",
            CapturedState =
            {
                [ArmouryCrateTweaks.OemWidgetJournalKey] = "Injected_abcdefghijklm",
                [ArmouryCrateTweaks.ScreenManagerJournalKey] = "0200",
                ["amd.svc.WinDefend"] = "Disabled",
                ["amd.task.\\EvilTask"] = "Ready",
                ["service.AsusAppService.start"] = "2"
            }
        };

        var dropped = TweakJournalService.DropUntrustedLegacyState(entry);

        Assert.Contains(ArmouryCrateTweaks.OemWidgetJournalKey, dropped);
        Assert.Contains(ArmouryCrateTweaks.ScreenManagerJournalKey, dropped);
        Assert.Contains("amd.svc.WinDefend", dropped);
        Assert.Contains("amd.task.\\EvilTask", dropped);
        Assert.Single(entry.CapturedState);
        Assert.Equal("2", entry.CapturedState["service.AsusAppService.start"]);
    }

    [Fact]
    public void BuildAmdTelemetryRevertLines_blocks_protected_non_auep_and_disabled_entries()
    {
        var lines = DebloatTweaks.BuildAmdTelemetryRevertLines(new Dictionary<string, string>
        {
            ["amd.svc.WinDefend"] = "Disabled",
            ["amd.svc.AUEP_Protected\nEvil"] = "Automatic",
            ["amd.svc.Spooler"] = "Automatic",
            ["amd.svc.AUEPDisabled"] = "Disabled",
            ["amd.svc.AUEPLauncher"] = "Automatic",
            ["amd.task.\\NotAmdTask"] = "Ready",
            ["amd.task.\\AMD\\AUEP\\Collect"] = "Ready"
        });

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("'AUEPLauncher'") && l.Contains("'Automatic'"));
        Assert.Contains(lines, l => l.Contains("'\\AMD\\AUEP\\Collect'"));
        Assert.DoesNotContain(lines, l => l.Contains("WinDefend") || l.Contains("Spooler") || l.Contains("Disabled") || l.Contains("NotAmdTask"));
    }

    [Fact]
    public void DiagnosticsReport_ReadLogTail_reads_ordinary_file()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"ho-diag-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(temp, "line one\nline two\n");
            Assert.Equal("line one\nline two\n", DiagnosticsReport.ReadLogTail(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void OAuth_redirect_checks_reject_http_scheme()
    {
        Assert.Null(GogAuth.CodeFrom(new Uri("http://embed.gog.com/on_login_success?code=abc")));
        Assert.Equal("abc", GogAuth.CodeFrom(new Uri("https://embed.gog.com/on_login_success?code=abc")));

        Assert.False(EpicAuth.IsRedirectPage(new Uri("http://www.epicgames.com/id/api/redirect?clientId=1")));
        Assert.True(EpicAuth.IsRedirectPage(new Uri("https://www.epicgames.com/id/api/redirect?clientId=1")));
        Assert.False(EpicAuth.IsPastSignIn(new Uri("http://store.epicgames.com/en-US/")));
        Assert.True(EpicAuth.IsPastSignIn(new Uri("https://store.epicgames.com/en-US/")));

        Assert.False(XboxAuth.IsRedirect(new Uri("http://login.live.com/oauth20_desktop.srf?code=1")));
        Assert.True(XboxAuth.IsRedirect(new Uri("https://login.live.com/oauth20_desktop.srf?code=1")));
    }

    [Fact]
    public void SteamAuth_IsSteamCommunityUrl_rejects_lookalike_and_non_https_urls()
    {
        Assert.True(SteamAuth.IsSteamCommunityUrl("https://steamcommunity.com/login/settoken"));
        Assert.True(SteamAuth.IsSteamCommunityUrl("https://checkout.steamcommunity.com/login/settoken"));
        Assert.False(SteamAuth.IsSteamCommunityUrl("http://steamcommunity.com/login/settoken"));
        Assert.False(SteamAuth.IsSteamCommunityUrl("https://evil.example/steamcommunity.com"));
        Assert.False(SteamAuth.IsSteamCommunityUrl("https://steamcommunity.com.evil.example/"));
    }

    [Fact]
    public void GogDepots_SafePath_and_SafeFullPath_block_traversal_and_trailing_dots()
    {
        Assert.Null(GogDepots.SafePath(@"..\..\Windows\System32\cmd.exe"));
        Assert.Null(GogDepots.SafePath(@"sub\.. \file.txt"));
        Assert.Null(GogDepots.SafePath(@"sub\. \file.txt"));
        Assert.Equal(@"saves\.keep", GogDepots.SafePath(@"saves/.keep"));

        var root = Path.Combine(Path.GetTempPath(), "gog-root");
        Assert.Null(GogDepots.SafeFullPath(root, @"..\other\file.txt"));
        Assert.NotNull(GogDepots.SafeFullPath(root, @"bin\game.exe"));
    }

    [Fact]
    public void Manifest_and_chunk_unpackers_reject_oversized_and_negative_lengths()
    {
        // Epic manifest header with absurd unpacked size (1 GB) should return null rather than throwing OOM.
        var manifest = new byte[41];
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(0, 4), 0x44BEC00Cu);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(4, 4), 41u);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(8, 4), 1_000_000_000u);
        manifest[36] = 1; // stored compressed
        Assert.Null(EpicManifests.Parse(manifest));

        // Epic chunk header with negative headerSize or oversized chunk.Size should throw InvalidDataException.
        var chunkFile = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkFile.AsSpan(0, 4), 0xB1FE3AA2u);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkFile.AsSpan(8, 4), 0xFFFFFFFFu);
        var chunk = new EpicChunk("00000000000000000000000000000000", 0, "00", 0, 1024, 64);
        Assert.Throws<InvalidDataException>(() => EpicManifests.UnpackChunk(chunkFile, chunk));

        var hugeChunk = new EpicChunk("00000000000000000000000000000000", 0, "00", 0, (uint)EpicManifests.MaxChunkBytes + 1, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkFile.AsSpan(8, 4), 41u);
        Assert.Throws<InvalidDataException>(() => EpicManifests.UnpackChunk(chunkFile, hugeChunk));

        // GOG chunk unpack rejects negative or oversized chunk sizes.
        Assert.Throws<InvalidDataException>(() => GogInstaller.Unpack([], new GogChunk("00", -1, "00", 0)));
        Assert.Throws<InvalidDataException>(() => GogInstaller.Unpack([], new GogChunk("00", GogInstaller.MaxChunkBytes + 1, "00", 0)));

        // GOG Unzlib enforces cap on non-zlib payloads too.
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write("{}"u8);
        }
        Assert.Equal("{}", GogContent.Unzlib(packed.ToArray()));
    }
}
