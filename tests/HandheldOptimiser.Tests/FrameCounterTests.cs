using HandheldOptimiser.PowerService;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Counting each program's frames from present events, including frames made inside AMD's display driver
/// by Fluid Motion Frames.
/// </summary>
public unsafe class FrameCounterTests
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly Guid PresentMon = new("ECAA4712-4644-442F-B94C-A32F6CF8A499");

    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OneSecondLater = Start.AddSeconds(1);

    private static void SendPresent(FrameCounter counter, Guid provider, ushort id, int processId)
    {
        var record = stackalloc byte[112];
        new Span<byte>(record, 112).Clear();
        *(int*)(record + 12) = processId;
        *(Guid*)(record + 24) = provider;
        *(ushort*)(record + 40) = id;
        counter.Record(record);
    }

    private static void SendFlipFrameType(FrameCounter counter, byte frameType, byte version = 0, ulong timeStamp = 100)
    {
        var data = stackalloc byte[25];
        new Span<byte>(data, 25).Clear();
        data[16] = frameType;
        *(ulong*)(data + 17) = timeStamp;

        var record = stackalloc byte[112];
        new Span<byte>(record, 112).Clear();
        *(int*)(record + 12) = 4; // Written from the display driver's interrupt handler, not the game's process.
        *(Guid*)(record + 24) = PresentMon;
        *(ushort*)(record + 40) = 0x02;
        record[42] = version;
        *(ushort*)(record + 86) = (ushort)(version >= 1 ? 25 : 17);
        *(byte**)(record + 96) = data;
        counter.Record(record);
    }

    [Fact]
    public void Runtime_presents_are_counted_ahead_of_the_kernels_for_the_same_program()
    {
        var counter = new FrameCounter(Start);
        for (var i = 0; i < 60; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 10);
            SendPresent(counter, DxgKrnl, 0xB8, processId: 10);
            SendPresent(counter, DxgKrnl, 0xB8, processId: 10);
        }

        SendPresent(counter, DxgKrnl, 0xB8, processId: 20);

        var frames = counter.Take(OneSecondLater).Shown;

        Assert.Equal(60, frames[10]);
        Assert.Equal(1, frames[20]);
    }

    [Fact]
    public void Fluid_motion_frames_are_added_to_the_games_own_frames()
    {
        var counter = new FrameCounter(Start);
        for (var i = 0; i < 45; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 10);
            SendFlipFrameType(counter, frameType: 1);
            SendFlipFrameType(counter, frameType: 100);
        }

        var frames = counter.Take(OneSecondLater).Shown;

        Assert.Equal(90, frames[10]);
    }

    [Fact]
    public void Rendered_frames_leave_out_fluid_motion_frames_so_tdp_engine_steers_by_what_the_game_draws()
    {
        var counter = new FrameCounter(Start);
        for (var i = 0; i < 45; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 10);
            SendFlipFrameType(counter, frameType: 1);
            SendFlipFrameType(counter, frameType: 100);
        }

        SendPresent(counter, Dxgi, 0x2A, processId: 20);

        var frames = counter.Take(OneSecondLater);

        Assert.Equal(45, frames.Rendered[10]);
        Assert.Equal(90, frames.Shown[10]);
        Assert.Equal(1, frames.Rendered[20]);
        Assert.Equal(1, frames.Shown[20]);
    }

    [Fact]
    public void Without_fluid_motion_frames_rendered_and_shown_are_the_same()
    {
        var counter = new FrameCounter(Start);
        for (var i = 0; i < 60; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 10);
        }

        var frames = counter.Take(OneSecondLater);

        Assert.Equal(60, frames.Rendered[10]);
        Assert.Equal(60, frames.Shown[10]);
    }

    [Fact]
    public void Fluid_motion_frames_go_to_the_program_whose_own_frames_match_the_drivers_original_flips()
    {
        var counter = new FrameCounter(Start);

        // The game at 40 rendered frames with AFMF on, while another window draws 60 and the overlay draws 1.
        for (var i = 0; i < 40; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 10);
            SendFlipFrameType(counter, frameType: 1);
            SendFlipFrameType(counter, frameType: 100);
        }

        for (var i = 0; i < 60; i++)
        {
            SendPresent(counter, Dxgi, 0x2A, processId: 20);
        }

        SendPresent(counter, Dxgi, 0x2A, processId: 30);

        var frames = counter.Take(OneSecondLater).Shown;

        Assert.Equal(80, frames[10]);
        Assert.Equal(60, frames[20]);
        Assert.Equal(1, frames[30]);
    }

    [Fact]
    public void Dropped_version_one_fluid_motion_frames_with_a_zero_time_stamp_do_not_count()
    {
        var counter = new FrameCounter(Start);
        SendPresent(counter, D3D9, 0x01, processId: 10);
        SendFlipFrameType(counter, frameType: 100, version: 1, timeStamp: 123456);
        SendFlipFrameType(counter, frameType: 100, version: 1, timeStamp: 0);

        var frames = counter.Take(OneSecondLater).Shown;

        Assert.Equal(2, frames[10]);
    }
}

