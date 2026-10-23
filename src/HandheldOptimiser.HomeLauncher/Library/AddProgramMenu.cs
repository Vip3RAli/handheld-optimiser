using System.IO;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Adding programs the stores do not list: picked from the Start menu with the controller, or with the
/// file picker by touch or mouse. A program added while the Apps tab is open goes under Apps, as does a
/// well known app such as Chrome or Discord; anything else goes with the games.
/// </summary>
internal sealed class AddProgramMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ILibraryHost _host;

    // Bumped each time the list is filled, so a slow, older fill does not land in a newer list.
    private int _listing;

    public AddProgramMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _host = host;

        view.AddProgramGroup.Click += (_, _) => Open();
        view.BrowseItem.Click += (_, _) => Browse();
        view.AddBackItem.Click += (_, _) => menu.Back();
    }

    private void Open()
    {
        _menu.ClearRows(_view.AddItems);
        _menu.Show(_view.AddItems, _state.Filter.Apps ? "Add an app" : "Add a program", SettingsMenu.GamesApps);
        _menu.Subtitle = "Looking through the Start menu...";
        _ = ListStartMenuProgramsAsync();
    }

    private async Task ListStartMenuProgramsAsync()
    {
        var listing = ++_listing;
        var folders = _state.AllTiles.Select(t => t.Game.InstallDirectory).OfType<string>().ToList();

        // Reading every shortcut takes a moment on a full Start menu.
        var programs = await Task.Run(() => AddedPrograms.StartMenuPrograms(folders));
        if (listing != _listing || !ReferenceEquals(_menu.List, _view.AddItems))
        {
            return;
        }

        _menu.Subtitle = programs.Count == 0 ? "No other programs in the Start menu"
            : _state.Filter.Apps ? "Or pick one from the Start menu. It goes under Apps"
            : "Or pick one from the Start menu";

        foreach (var (title, shortcut, _) in programs)
        {
            _menu.AddRow(_view.AddItems, _view.AddBackItem, title, null, () => Add(shortcut, title));
        }
    }

    private void Browse()
    {
        var picker = new OpenFileDialog
        {
            Title = _state.Filter.Apps ? "Add an app to the library" : "Add a program to the game library",
            Filter = AddedPrograms.BrowseFilter,

            // A shortcut is kept as it is, so its own name and arguments are used.
            DereferenceLinks = false,
            CheckFileExists = true
        };

        if (picker.ShowDialog(_host.Window) == true)
        {
            Add(picker.FileName, null);
        }
    }

    private void Add(string path, string? title)
    {
        _menu.Close();

        if (!File.Exists(path) || AddedPrograms.Add(path, title, _state.Filter.Apps ? true : null) is not { } game)
        {
            _notices.Show($"{title ?? Path.GetFileNameWithoutExtension(path)} could not be added.");
            return;
        }

        // The new tile takes the focus once the scan has found it, on the tab it is listed on.
        _state.FocusedKey = game.Key;
        var tab = game.IsApp ? TileFilter.AppsTab : _state.Filter.Apps ? TileFilter.All : _state.Filter;
        if (tab != _state.Filter)
        {
            _state.Filter = tab;
            _host.ShowTiles();
        }

        _notices.Show(game.IsApp ? $"{game.Title} added to Apps. Move to Games in its quick actions if it is a game."
            : $"{game.Title} added to the library.");
        _ = _host.ScanAsync();
    }
}
