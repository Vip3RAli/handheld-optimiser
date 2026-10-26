using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The Ally's GPU memory: the sizes on offer and the firmware's code for each, what is waiting for a
/// restart, and the note of the size Windows started with.
/// </summary>
public class GpuMemoryTests
{
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

    // ----- What the firmware answers -----

    [Fact]
    public void A_firmware_answer_with_the_presence_bit_gives_its_code() =>
        Assert.Equal(0x108, GpuMemory.CodeFromStatus(0x00010108));

    [Theory]
    [InlineData(0x00000108)]
    [InlineData(unchecked((int)0xFFFFFFFE))]
    [InlineData(int.MinValue)]
    public void A_model_without_it_answers_no_code(int answer) =>
        Assert.Null(GpuMemory.CodeFromStatus(answer));

    // ----- The size Windows has, from the graphics driver -----

    [Theory]
    [InlineData(6442450944L, 6)]
    [InlineData(1073741824L, 1)]
    [InlineData(8589934592L, 8)]
    [InlineData(536870912L, 0)]
    public void The_driver_reading_gives_the_size_Windows_has(long bytes, int size) =>
        Assert.Equal(size, GpuMemory.SizeFromBytes(bytes));

    [Fact]
    public void No_driver_reading_is_not_known() =>
        Assert.Null(GpuMemory.SizeFromBytes(null));

    // ----- What waits for a restart -----

    [Fact]
    public void Nothing_waits_when_the_firmware_has_what_Windows_has() =>
        Assert.Equal((6, (int?)null), GpuMemory.Describe(6, 0x108));

    [Fact]
    public void A_change_waits_for_a_restart() =>
        Assert.Equal((6, 4), GpuMemory.Describe(6, 0x105));

    [Fact]
    public void Auto_waits_for_a_restart_too() =>
        Assert.Equal((6, 0), GpuMemory.Describe(6, 0x100));

    [Fact]
    public void An_unknown_size_now_is_null_not_a_guess() =>
        Assert.Equal(((int?)null, 4), GpuMemory.Describe(null, 0x105));

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
