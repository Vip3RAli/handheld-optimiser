using System.Windows.Input;
using HandheldOptimiser.Views;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// A controller reaches this app as arrow keys. A tile's switch sits inside the tile, where the arrows
/// would never find it: Right from one tile goes to the next tile. So Right on a tile steps in to its
/// switch, and Left on the switch steps back out.
/// </summary>
public sealed class TileKeysTests
{
    [Fact]
    public void Right_on_a_tile_steps_in_to_its_switch() =>
        Assert.Equal(TileKeyMove.ToInner, TileKeys.Move(Key.Right, focusOnTile: true, hasInner: true));

    [Fact]
    public void Left_on_the_switch_steps_back_to_the_tile() =>
        Assert.Equal(TileKeyMove.ToTile, TileKeys.Move(Key.Left, focusOnTile: false, hasInner: true));

    /// <summary>From the switch, Right is left alone, so it carries on to the next tile.</summary>
    [Fact]
    public void Right_on_the_switch_moves_on() =>
        Assert.Equal(TileKeyMove.None, TileKeys.Move(Key.Right, focusOnTile: false, hasInner: true));

    /// <summary>
    /// WPF sends every arrow from a tile in to that tile's own switch first. Left would then bounce
    /// between the two for ever, and Up and Down would take two presses a row. So the other three
    /// arrows on a tile leave it, skipping its switch.
    /// </summary>
    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public void The_other_arrows_on_a_tile_leave_it(Key key) =>
        Assert.Equal(TileKeyMove.Out, TileKeys.Move(key, focusOnTile: true, hasInner: true));

    /// <summary>From the switch, Up and Down are left alone: WPF already finds the tile above or below.</summary>
    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public void Up_and_down_on_the_switch_move_on(Key key) =>
        Assert.Equal(TileKeyMove.None, TileKeys.Move(key, focusOnTile: false, hasInner: true));

    /// <summary>With no switch to skip, WPF's own movement from the tile is already right.</summary>
    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Right)]
    public void A_tile_with_no_switch_moves_on(Key key) =>
        Assert.Equal(TileKeyMove.None, TileKeys.Move(key, focusOnTile: true, hasInner: false));

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.Tab)]
    public void Other_keys_are_left_alone(Key key)
    {
        Assert.Equal(TileKeyMove.None, TileKeys.Move(key, focusOnTile: true, hasInner: true));
        Assert.Equal(TileKeyMove.None, TileKeys.Move(key, focusOnTile: false, hasInner: true));
    }
}
