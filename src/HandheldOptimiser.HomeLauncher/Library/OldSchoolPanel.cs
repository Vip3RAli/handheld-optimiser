using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Where each card of the Old School layout sits, by how far behind the front card it is. The picked
/// game is big at the front left and the next few recede to the right, each smaller and tucked behind
/// the one before, as the Xbox 360's cards did.
/// </summary>
internal static class OldSchoolStack
{
    // A card in DIPs, as LibraryResources.xaml draws it.
    public const double CardWidth = 420;
    public const double CardHeight = 320;

    // How many cards behind the front one are held with their pictures, and how many before it.
    private const int Ahead = 5;
    private const int Behind = 1;

    /// <summary>
    /// The size, left edge and opacity at each whole depth, with 0 the front card. The card just passed
    /// (-1) has grown towards the player and faded, and the one at the far end (5) has yet to fade in.
    /// The card passed barely moves left: the page cuts off anything past its edge, which would show.
    /// As on the NXE dashboard, the card behind the front one starts at its right edge and is seen
    /// whole, and each one further back is smaller and starts a little inside the right edge of the
    /// one before it, so a strip of every card shows.
    /// </summary>
    private static readonly (double Scale, double Left, double Opacity)[] Stops =
    [
        (1.12, -40, 0),
        (1.00, 0, 1),
        (0.74, 410, 1),
        (0.59, 667, 1),
        (0.49, 832, 1),
        (0.42, 947, 1),
        (0.36, 1040, 0)
    ];

    /// <summary>
    /// The same stops spread out as far as the Modern look takes them on a wide page: the cards behind
    /// shrink less and sit further apart, each still tucked a little behind the one before. The Modern look
    /// goes part way here, by as much as it needs for the last card on show to reach the page's right edge,
    /// so the stack spans the screen with the front card at its usual size (the user asked, 2026-11-13).
    /// </summary>
    private static readonly (double Scale, double Left, double Opacity)[] WideStops =
    [
        (1.12, -40, 0),
        (1.00, 0, 1),
        (0.84, 410, 1),
        (0.71, 730, 1),
        (0.60, 990, 1),
        (0.51, 1205, 1),
        (0.44, 1380, 0)
    ];

    // The depth of the first of the stops.
    private const int FirstDepth = -1;

    // The furthest back of the cards that are fully on show.
    private const int LastShown = 4;

    // The most the cards are enlarged to fill a wide screen. Less in the Console space look, which keeps
    // room under the cards for the floor they are reflected in.
    private const double MostFit = 1.75;
    private const double MostConsoleFit = 1.35;

    // The Modern look's cards are a tenth smaller than the Classic look's at the same width, and the stack
    // starts at the page's left edge and spreads out to its right (WideStops). The user asked for both on
    // 2026-11-13: moved in from the left instead, the stack looked as if someone had pressed Tab.
    private const double ConsoleSize = 0.9;

    /// <summary>
    /// The room under the cards for their reflections in the Console space look, as a part of a card's
    /// height: the reflection fades out at 32% of it, 8 under the card. The stack is that much taller, as
    /// the scroll viewer it sits in cuts off anything drawn outside it.
    /// </summary>
    public const double ReflectionRoom = 0.34;

    /// <summary>How much bigger than a card the tallest one is drawn: the one sliding out, which grows as it goes.</summary>
    public static readonly double Tallest = Stops.Max(s => s.Scale);

    /// <summary>
    /// How much bigger to draw the cards so they run the width there is, as the Xbox 360's filled the
    /// screen. Never smaller than they are designed, for the handheld's own screen.
    /// </summary>
    /// <param name="width">The room across, in the units the cards are sized in.</param>
    /// <param name="console">Whether the Console space look is on, which enlarges them less.</param>
    public static double Fit(double width, bool console = false) =>
        console ? Math.Clamp(double.IsFinite(width) ? width * ConsoleSize / StackWidth : 0, ConsoleSize, MostConsoleFit)
            : double.IsFinite(width) ? Math.Clamp(width / StackWidth, 1, MostFit) : 1;

    /// <summary>
    /// How far towards the wide stops the Modern look spreads the cards on a page that wide: just enough
    /// for the last card on show to end at the right edge, from 0 (as designed) to 1 (as wide as they go).
    /// </summary>
    /// <param name="width">The room across, in the units the cards are sized in.</param>
    public static double Spread(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return 0;
        }

        var room = width / Fit(width, console: true);
        return Math.Clamp((room - StackWidth) / (Right(WideStops) - StackWidth), 0, 1);
    }

    /// <summary>How wide the stack is at its designed size, from the front card's left edge to the right edge of the last card fully on show.</summary>
    private static double StackWidth => Right(Stops);

    private static double Right((double Scale, double Left, double Opacity)[] stops)
    {
        var (scale, left, _) = stops[LastShown - FirstDepth];
        return left + (scale * CardWidth);
    }

    /// <summary>The size, left edge and opacity of a card that many places behind the front one.</summary>
    /// <param name="depth">Its place less the front card's, which is part way between two while they move.</param>
    /// <param name="spread">How far towards the wide stops, from Spread.</param>
    public static (double Scale, double Left, double Opacity) Place(double depth, double spread = 0)
    {
        var at = Math.Clamp(depth - FirstDepth, 0, Stops.Length - 1);
        var before = (int)Math.Floor(at);
        var after = Math.Min(before + 1, Stops.Length - 1);
        var share = at - before;

        var (scale, left, opacity) = Stop(before, spread);
        var (nextScale, nextLeft, nextOpacity) = Stop(after, spread);
        return (scale + ((nextScale - scale) * share), left + ((nextLeft - left) * share), opacity + ((nextOpacity - opacity) * share));
    }

    private static (double Scale, double Left, double Opacity) Stop(int index, double spread)
    {
        var (scale, left, opacity) = Stops[index];
        if (spread <= 0)
        {
            return (scale, left, opacity);
        }

        var (wideScale, wideLeft, _) = WideStops[index];
        return (scale + ((wideScale - scale) * spread), left + ((wideLeft - left) * spread), opacity);
    }

    /// <summary>
    /// The first and last of the games whose cards can be on screen with that one at the front, which
    /// are the ones to hold pictures for. The last is before the first when there are no games.
    /// </summary>
    public static (int First, int Last) Window(int front, int count) =>
        count <= 0 ? (0, -1) : (Math.Clamp(front - Behind, 0, count - 1), Math.Clamp(front + Ahead, 0, count - 1));
}

/// <summary>
/// Lays the games out as the Old School stack of cards. Every card is arranged in the same place and
/// then moved and sized with a transform, so moving along them is one number changing, with no layout
/// pass for each step of the way.
/// </summary>
internal sealed class OldSchoolPanel : Panel
{
    /// <summary>
    /// Whether the Console space look is on, which every stack of cards draws in. Set by ConsoleSpace,
    /// which then lays the stacks out again.
    /// </summary>
    public static bool ConsoleSpace { get; set; }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(OldSchoolPanel),
        new FrameworkPropertyMetadata(0d, (panel, _) => ((OldSchoolPanel)panel).PlaceCards()));

    /// <summary>The place of the card at the front, which is part way between two while they move.</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>
    /// How near the front a card is, from 1 for the front card to 0 for the ones either side of it and
    /// beyond. Set on each card, for the name that only the front card shows.
    /// </summary>
    public static readonly DependencyProperty FrontProperty = DependencyProperty.RegisterAttached(
        "Front", typeof(double), typeof(OldSchoolPanel), new FrameworkPropertyMetadata(0d));

    public static double GetFront(DependencyObject card) => (double)card.GetValue(FrontProperty);

    public static void SetFront(DependencyObject card, double value) => card.SetValue(FrontProperty, value);

    /// <summary>How much bigger than designed the cards are drawn, to fill the width of the screen.</summary>
    public double Fit { get; private set; } = 1;

    /// <summary>How far the Modern look spreads the cards out to reach the right edge. See OldSchoolStack.Spread.</summary>
    public double Spread { get; private set; }

    /// <summary>
    /// How tall the cards in this panel are, as its tiles are drawn. The games' cards are the stack's own
    /// height; the deals' are shorter, the shape of a store's banner.
    /// </summary>
    public double CardHeight { get; set; } = OldSchoolStack.CardHeight;

    /// <summary>Where the front card rests, in the panel's own units.</summary>
    public Rect FrontCard => new(0, (OldSchoolStack.Tallest - 1) * CardHeight * Fit / 2, OldSchoolStack.CardWidth * Fit, CardHeight * Fit);

    /// <summary>How far across, in the panel's own units, a swipe moves the cards on by one.</summary>
    public double Step => OldSchoolStack.Place(1).Left * Fit;

    protected override Size MeasureOverride(Size availableSize)
    {
        var card = new Size(OldSchoolStack.CardWidth, CardHeight);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(card);
        }

        // As wide as there is room for, and tall enough for the card sliding out, which grows as it goes.
        Fit = OldSchoolStack.Fit(availableSize.Width, ConsoleSpace);
        var width = double.IsInfinity(availableSize.Width) ? card.Width : availableSize.Width;
        var reflections = ConsoleSpace ? card.Height * Fit * OldSchoolStack.ReflectionRoom : 0;
        return new Size(width, (card.Height * Fit * OldSchoolStack.Tallest) + reflections);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Fit = OldSchoolStack.Fit(finalSize.Width, ConsoleSpace);
        Spread = ConsoleSpace ? OldSchoolStack.Spread(finalSize.Width) : 0;
        var card = new Rect(0, 0, OldSchoolStack.CardWidth, CardHeight);
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Arrange(card);

            // An earlier game is always in front of a later one, the one sliding out included.
            SetZIndex(children[i], children.Count - i);
        }

        PlaceCards();
        return finalSize;
    }

    private void PlaceCards()
    {
        var position = Position;
        var fit = Fit;
        var spread = Spread;
        var height = CardHeight * fit;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var (scale, left, opacity) = OldSchoolStack.Place(i - position, spread);
            SetFront(child, Math.Max(0, 1 - Math.Abs(i - position)));

            // Most cards are out of sight, and stay as they are.
            if (opacity <= 0 && child.Opacity <= 0)
            {
                continue;
            }

            if (child.RenderTransform is not MatrixTransform transform || transform.IsFrozen)
            {
                child.RenderTransform = transform = new MatrixTransform();
            }

            // Sized about its own middle, so every card shares one centre line down the middle of the panel.
            transform.Matrix = new Matrix(scale * fit, 0, 0, scale * fit, left * fit, (OldSchoolStack.Tallest - scale) * height / 2);
            child.Opacity = opacity;

            // A card out of sight takes no taps, but can still be given the focus, which brings it forward.
            child.IsHitTestVisible = opacity > 0.5;
        }
    }

}

/// <summary>
/// Where each name in the Old School layout's list of categories sits. The one that is open is full
/// size just above the cards, and the others are stacked above it, each smaller and fainter than the one
/// below, as the Xbox 360's channels were. The list goes round, so there is always one above.
/// </summary>
internal static class OldSchoolList
{
    /// <summary>
    /// The size, the height above the open category's own line, and the opacity at each whole place up
    /// the list, with 0 the open one. The one just left (-1) has dropped behind the cards and faded.
    /// </summary>
    private static readonly (double Scale, double Up, double Opacity)[] Stops =
    [
        (1.00, -44, 0),
        (1.00, 0, 1),
        (0.78, 52, 0.75),
        (0.64, 93, 0.5),
        (0.53, 126, 0.32),
        (0.45, 152, 0.16),
        (0.40, 172, 0)
    ];

    private const int FirstPlace = -1;

    /// <summary>The room the list needs above the open category's own line, to its top.</summary>
    public static double Rise => Stops[^2].Up;

    /// <summary>
    /// Whether a list of that many goes round. A short one does not: the name dropping out of sight
    /// below would be the same one coming back into sight above.
    /// </summary>
    public static bool GoesRound(int count) => count >= Stops.Length - 1;

    /// <summary>How many places up the list a category is: how far it is past the open one, going round.</summary>
    /// <param name="position">The open category, which is part way between two while the list moves.</param>
    public static double Place(int index, double position, int count)
    {
        if (!GoesRound(count))
        {
            return index - position;
        }

        var place = (index - position) % count;
        place = place < 0 ? place + count : place;

        // The last of them is the one below the open one, on its way out of or into sight.
        return place > count - 1 ? place - count : place;
    }

    /// <summary>The size, height and opacity of a name that many places up the list.</summary>
    public static (double Scale, double Up, double Opacity) At(double place)
    {
        var at = Math.Clamp(place - FirstPlace, 0, Stops.Length - 1);
        var before = (int)Math.Floor(at);
        var after = Math.Min(before + 1, Stops.Length - 1);
        var share = at - before;

        var (scale, up, opacity) = Stops[before];
        var (nextScale, nextUp, nextOpacity) = Stops[after];
        return (scale + ((nextScale - scale) * share), up + ((nextUp - up) * share), opacity + ((nextOpacity - opacity) * share));
    }

    /// <summary>The steps from one category to another, up the list as positive: by the shorter way when it goes round.</summary>
    public static int Steps(int from, int to, int count)
    {
        if (!GoesRound(count))
        {
            return to - from;
        }

        var up = (((to - from) % count) + count) % count;
        return up <= count / 2 ? up : up - count;
    }

    /// <summary>The category a step up or down the list lands on, or the same one at the end of a list that does not go round.</summary>
    public static int Step(int from, int step, int count) =>
        count <= 0 ? 0
        : GoesRound(count) ? (((from + step) % count) + count) % count
        : Math.Clamp(from + step, 0, count - 1);
}

/// <summary>
/// Lays out the Old School list of categories. As with the cards, every name is arranged in one place
/// and then moved and sized with a transform, so moving along the list is one number changing.
/// </summary>
internal sealed class OldSchoolListPanel : Panel
{
    /// <summary>Whether the names other than the open one fade further back, as the Console space look has them.</summary>
    public static bool Recede { get; set; }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(OldSchoolListPanel),
        new FrameworkPropertyMetadata(0d, (panel, _) => ((OldSchoolListPanel)panel).PlaceNames()));

    /// <summary>
    /// The open category. Part way between two while the list moves, and not kept within the count: the
    /// list goes round, so one past the last is the first again.
    /// </summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>How nearly a name is the open one, from 1 for it to 0 for its neighbours and beyond, for its marker.</summary>
    public static readonly DependencyProperty OpenProperty = DependencyProperty.RegisterAttached(
        "Open", typeof(double), typeof(OldSchoolListPanel), new FrameworkPropertyMetadata(0d));

    public static double GetOpen(DependencyObject name) => (double)name.GetValue(OpenProperty);

    public static void SetOpen(DependencyObject name, double value) => name.SetValue(OpenProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var tallest = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, OldSchoolList.Rise + tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Every name on the bottom line, where the open one is read.
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(new Point(0, finalSize.Height - child.DesiredSize.Height), child.DesiredSize));
        }

        PlaceNames();
        return finalSize;
    }

    private void PlaceNames()
    {
        var position = Position;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var place = OldSchoolList.Place(i, position, children.Count);
            var (scale, up, opacity) = OldSchoolList.At(place);
            SetOpen(child, Math.Max(0, 1 - Math.Abs(place)));

            if (opacity <= 0 && child.Opacity <= 0)
            {
                continue;
            }

            if (child.RenderTransform is not MatrixTransform transform || transform.IsFrozen)
            {
                child.RenderTransform = transform = new MatrixTransform();
            }

            // Sized about its bottom left corner, so every name starts at the same edge.
            transform.Matrix = new Matrix(scale, 0, 0, scale, 0, (child.DesiredSize.Height * (1 - scale)) - up);
            child.Opacity = Recede ? opacity * (1 - (0.35 * Math.Clamp(place, 0, 1))) : opacity;
            child.IsHitTestVisible = opacity > 0.3;
        }
    }
}

/// <summary>
/// Follows a finger or the mouse dragged across a stack of Old School cards, which moves the cards
/// with it, and says where they should come to rest when it is let go.
/// </summary>
internal sealed class OldSchoolSwipe
{
    // How far across a press has to move before it is a swipe and not a tap, in the panel's units.
    private const double Slop = 14;

    // Past either end the cards follow a third as far, so the end can be felt.
    private const double EndDrag = 3;

    // How long the cards carry on for at the speed they were let go, in seconds.
    private const double Glide = 0.18;

    private double _fromX;
    private double _fromY;
    private double _fromPosition;
    private double _lastX;
    private long _lastTick;

    // Cards a second, smoothed over the last few moves.
    private double _speed;

    /// <summary>Whether the press has moved far enough across to be a swipe.</summary>
    public bool Swiping { get; private set; }

    public void Begin(double x, double y)
    {
        _fromX = x;
        _fromY = y;
        Swiping = false;
    }

    /// <summary>
    /// The press has moved. Null while it is still a tap; false when it has turned out to be a move up
    /// or down, which is the page's to scroll; true once it is a swipe, with the cards moved to match.
    /// </summary>
    /// <param name="tick">The time in milliseconds, from any steady clock.</param>
    public bool? Move(OldSchoolPanel panel, int count, double x, double y, long tick)
    {
        if (!Swiping)
        {
            var across = Math.Abs(x - _fromX);
            var down = Math.Abs(y - _fromY);
            if (down > Slop && down > across)
            {
                return false;
            }

            if (across < Slop)
            {
                return null;
            }

            // From where the cards are now, part way through a move or not, with that move stopped.
            Swiping = true;
            var at = panel.Position;
            panel.BeginAnimation(OldSchoolPanel.PositionProperty, null);
            panel.Position = at;
            (_fromX, _fromPosition, _lastX, _lastTick, _speed) = (x, at, x, tick, 0);
            return true;
        }

        panel.Position = Held(_fromPosition - ((x - _fromX) / panel.Step), count);
        if (tick > _lastTick)
        {
            var speed = -(x - _lastX) / panel.Step / ((tick - _lastTick) / 1000.0);
            _speed = (_speed + speed) / 2;
            (_lastX, _lastTick) = (x, tick);
        }

        return true;
    }

    /// <summary>The card to come to rest on when a swipe is let go: the nearest, a little on in the way it was moving.</summary>
    public int End(OldSchoolPanel panel, int count, long tick)
    {
        Swiping = false;

        // A finger that stopped before lifting carries nothing on.
        var speed = tick - _lastTick > 120 ? 0 : _speed;
        return Rest(panel.Position, speed, count);
    }

    /// <summary>Where the cards sit for a drag to that place: as dragged between the ends, and a third as far past them.</summary>
    public static double Held(double position, int count)
    {
        var last = Math.Max(0, count - 1);
        return position < 0 ? position / EndDrag
            : position > last ? last + ((position - last) / EndDrag)
            : position;
    }

    /// <summary>The card the cards settle on from that place, let go at that many cards a second.</summary>
    public static int Rest(double position, double speed, int count) =>
        (int)Math.Round(Math.Clamp(position + (speed * Glide), 0, Math.Max(0, count - 1)));
}
