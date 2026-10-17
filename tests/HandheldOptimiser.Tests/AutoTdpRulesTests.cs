using System.IO;
using System.Text;
using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The TDP Engine service's decisions that need no hardware: what a request from the library comes to,
/// whose frames count as the game's, and what it makes of a request it cannot read.
/// </summary>
public class AutoTdpRulesTests
{
    // ----- What a request comes to -----

    [Fact]
    public void A_request_with_nothing_set_comes_to_the_lowest_limits()
    {
        var (settings, start, folder) = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start));

        Assert.Equal(AutoTdpProtocol.LowestFps, settings.TargetFps);
        Assert.Equal(AutoTdpProtocol.LowestWatts, settings.MinWatts);
        Assert.Equal(AutoTdpProtocol.LowestWatts, settings.MaxWatts);
        Assert.Equal(AutoTdpProtocol.LowestTemperature, settings.TemperatureLimit);
        Assert.Equal(AutoTdpProtocol.LowestWatts, start);
        Assert.Null(folder);
    }

    [Fact]
    public void Negative_numbers_are_held_to_the_lowest_limits()
    {
        var (settings, start, _) = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start,
            TargetFps: -60, MinWatts: -5, MaxWatts: -1, TemperatureLimit: -40, StartWatts: -3));

        Assert.Equal(AutoTdpProtocol.LowestFps, settings.TargetFps);
        Assert.Equal(AutoTdpProtocol.LowestWatts, settings.MinWatts);
        Assert.Equal(AutoTdpProtocol.LowestWatts, settings.MaxWatts);
        Assert.Equal(AutoTdpProtocol.LowestTemperature, settings.TemperatureLimit);
        Assert.Equal(AutoTdpProtocol.LowestWatts, start);
    }

    [Fact]
    public void Numbers_past_the_range_are_held_to_the_highest_limits()
    {
        var (settings, start, _) = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start,
            TargetFps: 1000, MinWatts: 100, MaxWatts: 500, TemperatureLimit: 200, StartWatts: 99));

        Assert.Equal(AutoTdpProtocol.HighestFps, settings.TargetFps);
        Assert.Equal(AutoTdpProtocol.HighestWatts, settings.MinWatts);
        Assert.Equal(AutoTdpProtocol.HighestWatts, settings.MaxWatts);
        Assert.Equal(AutoTdpProtocol.HighestTemperature, settings.TemperatureLimit);
        Assert.Equal(AutoTdpProtocol.HighestWatts, start);
    }

    [Fact]
    public void A_highest_limit_under_the_lowest_is_raised_to_it()
    {
        var (settings, start, _) = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start,
            TargetFps: 60, MinWatts: 20, MaxWatts: 10, TemperatureLimit: 80));

        Assert.Equal(20, settings.MinWatts);
        Assert.Equal(20, settings.MaxWatts);
        Assert.Equal(20, start);
    }

    [Fact]
    public void Without_a_starting_point_it_starts_halfway_and_otherwise_inside_the_limits()
    {
        var halfway = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start, 60, 7, 25, 80));
        var over = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start, 60, 7, 25, 80, StartWatts: 28));
        var learned = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start, 60, 7, 25, 80, StartWatts: 12));

        Assert.Equal(16, halfway.StartWatts);
        Assert.Equal(25, over.StartWatts);
        Assert.Equal(12, learned.StartWatts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_game_folder_counts_the_busiest_program(string? folder)
    {
        var (_, _, gameFolder) = AutoTdpRules.FromRequest(new AutoTdpRequest(AutoTdpProtocol.Start, GameFolder: folder));

        Assert.Null(gameFolder);
    }

    // ----- Whose frames are the game's -----

    private static readonly Dictionary<int, string> Paths = new()
    {
        [1] = @"C:\Windows\System32\dwm.exe",
        [2] = @"C:\Games\Foo\bin\foo.exe",
        [3] = @"C:\Games\FooBar\foobar.exe",
        [4] = @"C:\Program Files\Browser\browser.exe",
        [5] = @"C:\Games\Foo\launcher.exe"
    };

    private static string? PathOf(int processId) => Paths.GetValueOrDefault(processId);

    [Fact]
    public void With_a_folder_the_busiest_program_inside_it_counts()
    {
        var frames = new Dictionary<int, double> { [1] = 240, [2] = 58, [4] = 120, [5] = 30 };

        Assert.Equal(58, AutoTdpRules.GameFps(frames, @"C:\Games\Foo", PathOf));
    }

    [Fact]
    public void A_folder_does_not_take_in_one_whose_name_starts_the_same()
    {
        var frames = new Dictionary<int, double> { [3] = 90 };

        Assert.Equal(0, AutoTdpRules.GameFps(frames, @"C:\Games\Foo", PathOf));
    }

    [Theory]
    [InlineData(@"C:\Games\Foo\")]
    [InlineData(@"c:\games\foo")]
    public void A_folder_matches_with_a_separator_on_the_end_and_in_any_case(string folder)
    {
        var frames = new Dictionary<int, double> { [2] = 58 };

        Assert.Equal(58, AutoTdpRules.GameFps(frames, folder, PathOf));
    }

    [Fact]
    public void Without_a_folder_the_busiest_program_but_the_compositor_counts()
    {
        var frames = new Dictionary<int, double> { [1] = 240, [2] = 58, [4] = 120 };

        Assert.Equal(120, AutoTdpRules.GameFps(frames, null, PathOf));
    }

    [Fact]
    public void Programs_whose_path_cannot_be_read_do_not_count()
    {
        var frames = new Dictionary<int, double> { [99] = 300, [2] = 58 };

        Assert.Equal(58, AutoTdpRules.GameFps(frames, null, PathOf));
        Assert.Equal(0, AutoTdpRules.GameFps(new Dictionary<int, double> { [99] = 300 }, null, PathOf));
    }

    // ----- Requests it cannot read -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"start\"")]
    [InlineData("{\"Command\": 5}")]
    [InlineData("{\"Command\": \"start\", \"TargetFps\": \"sixty\"}")]
    [InlineData("{\"Command\": \"start\", \"TargetFps\": 1e99}")]
    [InlineData("{\"Command\": \"start\", \"MaxWatts\": 99999999999}")]
    public void A_request_it_cannot_read_comes_to_nothing(string? line)
    {
        Assert.Null(AutoTdpProtocol.Read<AutoTdpRequest>(line));
    }

    [Fact]
    public void A_request_with_only_a_command_reads_with_the_rest_unset()
    {
        var request = AutoTdpProtocol.Read<AutoTdpRequest>("{\"Command\":\"start\"}");

        Assert.NotNull(request);
        Assert.Equal(AutoTdpProtocol.Start, request.Command);
        Assert.Equal(0, request.TargetFps);
        Assert.Null(request.GameFolder);
    }

    [Fact]
    public void A_request_round_trips()
    {
        var sent = new AutoTdpRequest(AutoTdpProtocol.Start, 60, 7, 25, 80, 14, @"C:\Games\Foo");

        Assert.Equal(sent, AutoTdpProtocol.Read<AutoTdpRequest>(AutoTdpProtocol.Write(sent)));
    }

    // ----- Reading a line off the pipe -----

    private static Task<string?> ReadLine(string text, int limit = AutoTdpRules.MaxRequestBytes) =>
        AutoTdpRules.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None, limit);

    [Fact]
    public async Task A_line_is_read_up_to_its_newline()
    {
        Assert.Equal("{\"Command\":\"status\"}", await ReadLine("{\"Command\":\"status\"}\r\nanything after"));
        Assert.Equal("abc", await ReadLine("abc\n"));
    }

    [Fact]
    public async Task A_line_without_a_newline_is_what_came()
    {
        Assert.Equal("abc", await ReadLine("abc"));
        Assert.Null(await ReadLine(""));
    }

    [Fact]
    public async Task A_line_longer_than_the_limit_is_not_read()
    {
        Assert.Null(await ReadLine(new string('x', 100), limit: 16));
        Assert.Null(await ReadLine(new string('x', 100) + "\n", limit: 16));
        Assert.Equal("short", await ReadLine("short\n" + new string('x', 100), limit: 16));
    }

    [Fact]
    public void Control_characters_are_taken_out_of_text_for_the_log()
    {
        Assert.Equal(@"C:\Games\Foo?[fake] line?", AutoTdpRules.Printable("C:\\Games\\Foo\n[fake] line\r"));
        Assert.Equal(@"C:\Games\Foo", AutoTdpRules.Printable(@"C:\Games\Foo"));
    }
}
