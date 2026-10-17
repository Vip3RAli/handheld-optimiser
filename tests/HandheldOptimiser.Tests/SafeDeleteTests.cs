using System.IO;
using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The check that decides whether a file the cleaner has opened is still inside the folder being
/// cleaned. Windows reports where a handle leads in the \\?\ form and in whatever case the names are
/// stored with, so both have to compare equal to the plain path the cleaner started from.
/// </summary>
public sealed class SafeDeleteTests
{
    private const string Root = @"C:\Users\Player\AppData\Local\CrashDumps";

    [Theory]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps\game.exe.1234.dmp")]
    [InlineData(@"\\?\C:\Users\Player\AppData\Local\CrashDumps\game.exe.1234.dmp")]
    [InlineData(@"\\?\c:\users\player\appdata\local\crashdumps\GAME.EXE.1234.DMP")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps\sub\deeper\file.dmp")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps\sub\")]
    public void A_path_inside_the_root_is_allowed(string finalPath)
    {
        Assert.True(SafeDelete.IsWithinRoot(Root, finalPath));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"\\?\C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumpsEvil\file.dmp")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps2")]
    [InlineData(@"C:\Users\Player\AppData\Local")]
    [InlineData(@"D:\Users\Player\AppData\Local\CrashDumps\file.dmp")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\Users\Player\AppData\Local\CrashDumps\file.dmp")]
    [InlineData(@"\\?\UNC\server\share\CrashDumps\file.dmp")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps\..\..\..\..\..\Windows\win.ini")]
    [InlineData(@"C:\Users\Player\AppData\Local\CrashDumps\.\file.dmp")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_path_outside_the_root_or_that_cannot_be_compared_is_refused(string? finalPath)
    {
        Assert.False(SafeDelete.IsWithinRoot(Root, finalPath));
    }

    [Fact]
    public void The_root_itself_is_allowed_only_when_asked_for()
    {
        // A single file such as MEMORY.DMP is its own root. A folder being cleaned never is removed.
        Assert.True(SafeDelete.IsWithinRoot(Root, @"\\?\" + Root, allowRootItself: true));
        Assert.False(SafeDelete.IsWithinRoot(Root, @"\\?\" + Root, allowRootItself: false));
        Assert.False(SafeDelete.IsWithinRoot(Root, Root + @"\", allowRootItself: false));
    }

    [Fact]
    public void The_root_may_be_given_in_the_long_form_or_with_a_trailing_backslash()
    {
        Assert.True(SafeDelete.IsWithinRoot(@"\\?\" + Root, Root + @"\file.dmp"));
        Assert.True(SafeDelete.IsWithinRoot(Root + @"\", @"\\?\" + Root + @"\file.dmp"));
    }

    [Fact]
    public void An_empty_root_allows_nothing()
    {
        Assert.False(SafeDelete.IsWithinRoot("", @"C:\file.dmp"));
        Assert.False(SafeDelete.IsWithinRoot(@"\\?\", @"C:\file.dmp"));
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows\Minidump", @"C:\Windows\Minidump")]
    [InlineData(@"\\?\UNC\server\share\folder", @"\\server\share\folder")]
    [InlineData(@"\\.\C:\Windows\Minidump\", @"C:\Windows\Minidump")]
    [InlineData(@"C:\Windows\Minidump", @"C:\Windows\Minidump")]
    public void Final_paths_are_turned_into_plain_drive_paths(string reported, string expected)
    {
        Assert.Equal(expected, SafeDelete.NormaliseFinalPath(reported));
    }

    [Fact]
    public void A_real_file_outside_the_cleanable_folders_is_refused_and_left_in_place()
    {
        // Even when the root it is checked against is its own folder, the handle has to lead inside the
        // folders SafetyGuard allows, and the temp folder is not one of them.
        var dir = Path.Combine(Path.GetTempPath(), "HandheldOptimiserTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "keep.dmp");
        File.WriteAllText(file, "not a cache");

        try
        {
            var outcome = SafeDelete.DeleteFile(file, dir, out var bytes, out var problem);

            Assert.Equal(SafeDeleteOutcome.Refused, outcome);
            Assert.Equal(0, bytes);
            Assert.NotNull(problem);
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_file_that_is_not_there_is_reported_missing()
    {
        var file = Path.Combine(Path.GetTempPath(), "HandheldOptimiserTests-" + Guid.NewGuid().ToString("N") + ".dmp");

        Assert.Equal(SafeDeleteOutcome.Missing, SafeDelete.DeleteFile(file, Path.GetTempPath(), out _, out _));
    }
}
