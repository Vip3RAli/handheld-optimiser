using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The Ally's GPU memory: the sizes on offer and the firmware's code for each, what is waiting for a
/// restart, and the note of the size Windows started with.
/// </summary>
public class GpuMemoryTests
{
    private static readonly DateTime Started = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    // ----- The sizes and their codes -----

    [Theory]
    [InlineData(0, 0x100)]
    [InlineData(1, 0x102)]
    [InlineData(2, 0x103)]
    [InlineData(3, 0x104)]
    [InlineData(4, 0x105)]
    [InlineData(5, 0x107)]
    [InlineData(6, 0x108)]
    [InlineData(7, 0x109)]
    [InlineData(8, 0x106)]
    public void Every_size_has_a_code_and_back(int size, int code)
    {
        Assert.Equal(code, GpuMemory.CodeOf(size));
        Assert.Equal(size, GpuMemory.SizeOf(code));
    }

    [Fact]
    public void The_sizes_on_offer_are_Auto_then_1_to_8_GB() =>
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], GpuMemory.Sizes);

    [Theory]
    [InlineData(0x101)]
    [InlineData(0x10A)]
    [InlineData(0)]
    public void A_code_not_in_the_table_is_unknown(int code) =>
        Assert.Null(GpuMemory.SizeOf(code));

    [Theory]
    [InlineData(9)]
    [InlineData(-1)]
    public void A_size_not_on_offer_has_no_code(int size) =>
        Assert.Null(GpuMemory.CodeOf(size));

    // ----- What waits for a restart -----

    [Fact]
    public void Nothing_waits_when_the_firmware_has_what_Windows_started_with() =>
        Assert.Equal((6, (int?)null), GpuMemory.Describe(0x108, 0x108));

    [Fact]
    public void A_change_waits_for_a_restart() =>
        Assert.Equal((6, 4), GpuMemory.Describe(0x108, 0x105));

    [Fact]
    public void An_unknown_size_now_is_null_not_a_guess() =>
        Assert.Equal(((int?)null, 4), GpuMemory.Describe(0x10A, 0x105));

    // ----- The note of the size Windows started with -----

    [Fact]
    public void A_note_from_this_start_is_used() =>
        Assert.Equal(0x108, GpuMemory.BootCode(0x108, Started.AddSeconds(30).Ticks, Started));

    [Fact]
    public void A_note_from_before_a_restart_is_not_used() =>
        Assert.Null(GpuMemory.BootCode(0x108, Started.AddHours(-5).Ticks, Started));

    [Fact]
    public void A_note_missing_its_code_or_its_time_is_not_used()
    {
        Assert.Null(GpuMemory.BootCode(null, Started.Ticks, Started));
        Assert.Null(GpuMemory.BootCode(0x108, null, Started));
    }

    // ----- What the app's row says -----

    [Theory]
    [InlineData(0, null, "Auto")]
    [InlineData(6, null, "6 GB")]
    [InlineData(6, 4, "6 GB, 4 GB after restart")]
    [InlineData(6, 0, "6 GB, Auto after restart")]
    [InlineData(null, null, "Unknown")]
    public void The_row_says_what_Windows_has_and_what_waits(int? now, int? next, string text) =>
        Assert.Equal(text, HandheldOptimiser.ViewModels.GpuMemoryText.Describe(now, next));

    // ----- The requests the widget and the app send -----

    [Fact]
    public void A_request_to_set_the_GPU_memory_reads_back()
    {
        var request = AutoTdpProtocol.Read<AutoTdpRequest>(
            $"{{\"{AutoTdpProtocol.Fields.Command}\":\"{AutoTdpProtocol.SetGpuMemory}\",\"{AutoTdpProtocol.Fields.GpuMemory}\":4}}");

        Assert.Equal(AutoTdpProtocol.SetGpuMemory, request?.Command);
        Assert.Equal(4, request?.GpuMemory);
    }

    [Fact]
    public void A_status_carries_the_GPU_memory()
    {
        var status = AutoTdpProtocol.Read<AutoTdpStatus>(AutoTdpProtocol.Write(
            new AutoTdpStatus(false, GpuMemoryNow: 6, GpuMemoryNext: 4, GpuMemoryOffered: GpuMemory.Sizes)));

        Assert.Equal(6, status?.GpuMemoryNow);
        Assert.Equal(4, status?.GpuMemoryNext);
        Assert.Equal(GpuMemory.Sizes, status?.GpuMemoryOffered);
    }

    [Fact]
    public void The_status_names_its_GPU_memory_fields_as_the_widget_reads_them()
    {
        var json = AutoTdpProtocol.Write(new AutoTdpStatus(false, GpuMemoryNow: 6, GpuMemoryNext: 4, GpuMemoryOffered: [0, 1]));

        Assert.Contains($"\"{AutoTdpProtocol.Fields.GpuMemoryNow}\":6", json);
        Assert.Contains($"\"{AutoTdpProtocol.Fields.GpuMemoryNext}\":4", json);
        Assert.Contains($"\"{AutoTdpProtocol.Fields.GpuMemoryOffered}\":[0,1]", json);
    }
}
