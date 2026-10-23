using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>Where the library thinks the player is, and where their presses go.</summary>
public sealed class LibraryNavigationTests
{
    private readonly List<string> _log = [];
    private readonly LibraryNavigation _navigation;

    public LibraryNavigationTests()
    {
        _navigation = new LibraryNavigation(_log.Add);
        _navigation.Return();
    }

    [Fact]
    public void Nothing_takes_input_until_the_library_is_in_front()
    {
        var navigation = new LibraryNavigation();
        Assert.Equal(InputTarget.Nowhere, navigation.Input);
        Assert.False(navigation.MayRestoreFocus(leavingSearch: true));

        navigation.Return();
        Assert.Equal(InputTarget.Page, navigation.Input);
        Assert.True(navigation.MayRestoreFocus(leavingSearch: false));
    }

    [Fact]
    public void The_menu_takes_input_and_keeps_background_jobs_off_the_focus()
    {
        Assert.True(_navigation.OpenMenu());
        Assert.Equal(InputTarget.Menu, _navigation.Input);
        Assert.False(_navigation.MayRestoreFocus(leavingSearch: false));
        Assert.False(_navigation.MayRestoreFocus(leavingSearch: true));

        _navigation.CloseMenu();
        Assert.Equal(InputTarget.Page, _navigation.Input);
        Assert.True(_navigation.MayRestoreFocus(leavingSearch: false));
    }

    [Fact]
    public void Moving_between_lists_keeps_the_menu_open() =>
        Assert.True(_navigation.OpenMenu() && _navigation.OpenMenu() && _navigation.MenuOpen);

    [Fact]
    public void The_text_box_opens_only_from_the_menu_and_goes_back_to_it()
    {
        Assert.False(_navigation.BeginEdit());
        Assert.Single(_log);

        _navigation.OpenMenu();
        Assert.True(_navigation.BeginEdit());
        Assert.Equal(InputTarget.TextBox, _navigation.Input);
        Assert.True(_navigation.MenuOpen);

        _navigation.EndEdit();
        Assert.Equal(InputTarget.Menu, _navigation.Input);
    }

    [Fact]
    public void Closing_the_menu_closes_its_text_box_too()
    {
        _navigation.OpenMenu();
        _navigation.BeginEdit();
        _navigation.CloseMenu();
        Assert.Equal(LibraryLayer.None, _navigation.Layer);
    }

    [Fact]
    public void The_menu_does_not_open_over_the_search_box_or_from_the_text_box()
    {
        _navigation.BeginTyping();
        Assert.False(_navigation.OpenMenu());
        Assert.Equal(InputTarget.SearchBox, _navigation.Input);

        _navigation.EndTyping();
        _navigation.OpenMenu();
        _navigation.BeginEdit();
        Assert.False(_navigation.OpenMenu());
        Assert.Equal(InputTarget.TextBox, _navigation.Input);
    }

    [Fact]
    public void Typing_lets_the_player_leave_for_the_games_found_but_not_a_background_job()
    {
        Assert.True(_navigation.BeginTyping());
        Assert.False(_navigation.MayRestoreFocus(leavingSearch: false));
        Assert.True(_navigation.MayRestoreFocus(leavingSearch: true));

        _navigation.EndTyping();
        Assert.Equal(InputTarget.Page, _navigation.Input);
    }

    [Fact]
    public void Typing_does_not_begin_while_the_menu_is_open()
    {
        _navigation.OpenMenu();
        Assert.False(_navigation.BeginTyping());
        Assert.Equal(LibraryLayer.Menu, _navigation.Layer);
    }

    [Fact]
    public void Ending_what_was_not_begun_changes_nothing()
    {
        _navigation.OpenMenu();
        _navigation.EndTyping();
        _navigation.EndEdit();
        Assert.Equal(LibraryLayer.Menu, _navigation.Layer);
        Assert.Empty(_log);
    }

    [Fact]
    public void Nothing_is_pressed_while_the_card_grows_but_it_may_take_the_focus()
    {
        Assert.True(_navigation.BeginCardMove());
        Assert.Equal(InputTarget.Nowhere, _navigation.Input);

        // The games are put in the card, and the focus among them, before it has finished.
        Assert.True(_navigation.MayRestoreFocus(leavingSearch: false));

        _navigation.SetCardOpen(true);
        _navigation.EndCardMove();
        Assert.Equal(LibraryPlace.NotInstalledCard, _navigation.Place);
        Assert.Equal(InputTarget.Page, _navigation.Input);
    }

    [Fact]
    public void The_card_does_not_grow_twice_or_under_the_menu()
    {
        _navigation.BeginCardMove();
        Assert.False(_navigation.BeginCardMove());
        _navigation.EndCardMove();

        _navigation.OpenMenu();
        Assert.False(_navigation.BeginCardMove());
        Assert.False(_navigation.CardMoving);
    }

    [Fact]
    public void Going_behind_another_window_stops_input_and_focus_moves()
    {
        _navigation.Leave();
        Assert.Equal(InputTarget.Nowhere, _navigation.Input);
        Assert.False(_navigation.MayRestoreFocus(leavingSearch: true));

        _navigation.Return();
        Assert.Equal(InputTarget.Page, _navigation.Input);
    }

    [Fact]
    public void A_refused_move_says_where_the_player_was()
    {
        _navigation.OpenMenu();
        _navigation.BeginTyping();
        Assert.Equal("Navigation: did not type in the search box, as in front, on the page, menu", Assert.Single(_log));
    }
}
