namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>Where on the screen the player is: the page, or the Old School layout's open Not installed card.</summary>
internal enum LibraryPlace
{
    /// <summary>The grid, the row or the Old School cards, with what is around them.</summary>
    Page,

    /// <summary>The Old School layout's Not installed card, opened to hold its grid.</summary>
    NotInstalledCard
}

/// <summary>What is over the page and has the input.</summary>
internal enum LibraryLayer
{
    None,

    /// <summary>The search box has the keyboard.</summary>
    Typing,

    /// <summary>The menu is open.</summary>
    Menu,

    /// <summary>The menu's text box is open.</summary>
    Editing
}

/// <summary>Where a press of the controller or a key goes.</summary>
internal enum InputTarget
{
    /// <summary>Nowhere: the library is behind another window, or the Not installed card is on its way open.</summary>
    Nowhere,

    /// <summary>The menu's text box, which takes every key but Enter and Escape.</summary>
    TextBox,

    /// <summary>The search box, which takes every key but Enter, Down and Escape.</summary>
    SearchBox,

    Menu,
    Page
}

/// <summary>
/// Where the player is in the library and what has the input, kept in one place. The window used to work
/// this out each time from half a dozen fields; now each change goes through one of the moves here, and a
/// move that makes no sense from where the player is is refused and logged rather than half done.
/// </summary>
/// <remarks>
/// Where on the page the focus is (a game, the top bar, Last played, the deals) is not kept here. The
/// keyboard focus already says that, and a second copy could only disagree with it.
/// </remarks>
internal sealed class LibraryNavigation(Action<string>? log = null)
{
    /// <summary>Whether the library is the window in front.</summary>
    public bool InFront { get; private set; }

    /// <summary>Whether the Old School layout's Not installed card is open.</summary>
    public bool CardOpen { get; private set; }

    /// <summary>The Not installed card is growing: nothing is pressed or moved to until its games are in.</summary>
    public bool CardMoving { get; private set; }

    public LibraryLayer Layer { get; private set; }

    public LibraryPlace Place => CardOpen ? LibraryPlace.NotInstalledCard : LibraryPlace.Page;

    /// <summary>Whether the menu is open, with or without its text box.</summary>
    public bool MenuOpen => Layer is LibraryLayer.Menu or LibraryLayer.Editing;

    /// <summary>Whether the search box has the keyboard.</summary>
    public bool Typing => Layer == LibraryLayer.Typing;

    public InputTarget Input => !InFront || CardMoving ? InputTarget.Nowhere
        : Layer switch
        {
            LibraryLayer.Editing => InputTarget.TextBox,
            LibraryLayer.Typing => InputTarget.SearchBox,
            LibraryLayer.Menu => InputTarget.Menu,
            _ => InputTarget.Page
        };

    /// <summary>
    /// Whether the focus may be put back on the page. Something finishing in the background may only do
    /// that while nothing is over the page. Leaving the search box for the games it found may, as that is
    /// the player asking.
    /// </summary>
    public bool MayRestoreFocus(bool leavingSearch) =>
        InFront && (Layer == LibraryLayer.None || (leavingSearch && Layer == LibraryLayer.Typing));

    /// <summary>The library has come to the front.</summary>
    public void Return() => InFront = true;

    /// <summary>Another window has come in front of the library.</summary>
    public void Leave() => InFront = false;

    /// <summary>Opens the menu, or moves to another of its lists. Not while typing or in the text box.</summary>
    public bool OpenMenu()
    {
        if (Layer is LibraryLayer.Typing or LibraryLayer.Editing)
        {
            return Refuse("open the menu");
        }

        Layer = LibraryLayer.Menu;
        return true;
    }

    /// <summary>Closes the menu, and its text box with it. Nothing happens when it is not open.</summary>
    public void CloseMenu()
    {
        if (MenuOpen)
        {
            Layer = LibraryLayer.None;
        }
    }

    /// <summary>Opens the menu's text box, in place of its list.</summary>
    public bool BeginEdit()
    {
        if (Layer != LibraryLayer.Menu)
        {
            return Refuse("open the text box");
        }

        Layer = LibraryLayer.Editing;
        return true;
    }

    /// <summary>Back from the text box to the menu's list. Nothing happens when the box is not open.</summary>
    public void EndEdit()
    {
        if (Layer == LibraryLayer.Editing)
        {
            Layer = LibraryLayer.Menu;
        }
    }

    /// <summary>The search box has taken the keyboard. Not while the menu is open.</summary>
    public bool BeginTyping()
    {
        if (MenuOpen)
        {
            return Refuse("type in the search box");
        }

        Layer = LibraryLayer.Typing;
        return true;
    }

    /// <summary>The search box has let go of the keyboard. Nothing happens when it did not have it.</summary>
    public void EndTyping()
    {
        if (Layer == LibraryLayer.Typing)
        {
            Layer = LibraryLayer.None;
        }
    }

    /// <summary>The Not installed card starts to grow. Not while the menu is open or it is already moving.</summary>
    public bool BeginCardMove()
    {
        if (CardMoving || MenuOpen)
        {
            return Refuse("open the Not installed card");
        }

        CardMoving = true;
        return true;
    }

    /// <summary>The card has finished moving, or was stopped part way.</summary>
    public void EndCardMove() => CardMoving = false;

    /// <summary>The page has been laid out inside the open card, or out of it again.</summary>
    public void SetCardOpen(bool open) => CardOpen = open;

    private bool Refuse(string move)
    {
        log?.Invoke($"Navigation: did not {move}, as {Describe()}");
        return false;
    }

    /// <summary>Where the player is, for the log.</summary>
    public string Describe() =>
        $"{(InFront ? "in front" : "behind another window")}, on the {(CardOpen ? "open Not installed card" : "page")}"
        + (CardMoving ? ", card moving" : string.Empty)
        + (Layer == LibraryLayer.None ? string.Empty : $", {Layer.ToString().ToLowerInvariant()}");
}
