using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Adding programs the stores do not list: picked from the Start menu with the controller, or with the
/// file picker by touch or mouse. A program added while the Apps tab is open goes under Apps, as does a
/// well known app such as Chrome or Discord; anything else goes with the games.
/// </summary>
public partial class LibraryWindow
{
    // A row per Start menu program, rebuilt each time the list opens.
    private readonly List<Button> _programRows = [];
    private int _programListing;

    private void OnOpenAddProgram(object sender, RoutedEventArgs e)
    {
        foreach (var row in _programRows)
        {
            AddItems.Children.Remove(row);
        }

        _programRows.Clear();
        OpenSettingsGroup(AddItems, _filter.Apps ? "Add an app" : "Add a program");
        MenuStore.Text = "Looking through the Start menu...";
        _ = ListStartMenuProgramsAsync();
    }

    private async Task ListStartMenuProgramsAsync()
    {
        var listing = ++_programListing;
        var folders = _allTiles.Select(t => t.Game.InstallDirectory).OfType<string>().ToList();

        // Reading every shortcut takes a moment on a full Start menu.
        var programs = await Task.Run(() => AddedPrograms.StartMenuPrograms(folders));
        if (listing != _programListing || !ReferenceEquals(_menu, AddItems))
        {
            return;
        }

        MenuStore.Text = programs.Count == 0 ? "No other programs in the Start menu"
            : _filter.Apps ? "Or pick one from the Start menu. It goes under Apps"
            : "Or pick one from the Start menu";

        var index = AddItems.Children.IndexOf(AddBackItem);
        foreach (var (title, shortcut, target) in programs)
        {
            var row = new Button
            {
                Style = (Style)FindResource("MenuButton"),
                Content = title,
                Tag = target
            };

            row.Click += (_, _) => AddProgram(shortcut, title);
            AddItems.Children.Insert(index++, row);
            _programRows.Add(row);
        }
    }

    private void OnBrowseProgram(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = _filter.Apps ? "Add an app to the library" : "Add a program to the game library",
            Filter = AddedPrograms.BrowseFilter,

            // A shortcut is kept as it is, so its own name and arguments are used.
            DereferenceLinks = false,
            CheckFileExists = true
        };

        if (picker.ShowDialog(this) == true)
        {
            AddProgram(picker.FileName, null);
        }
    }

    private void AddProgram(string path, string? title)
    {
        CloseMenu();

        if (!File.Exists(path) || AddedPrograms.Add(path, title, _filter.Apps ? true : null) is not { } game)
        {
            StatusText.Text = $"{title ?? Path.GetFileNameWithoutExtension(path)} could not be added.";
            return;
        }

        // The new tile takes the focus once the scan has found it, on the tab it is listed on.
        _focusedKey = game.Key;
        var tab = game.IsApp ? TileFilter.AppsTab : _filter.Apps ? TileFilter.All : _filter;
        if (tab != _filter)
        {
            _filter = tab;
            ApplyFilter();
        }

        StatusText.Text = game.IsApp ? $"{game.Title} added to Apps. Move to games in its quick actions if it is a game."
            : $"{game.Title} added to the library.";
        _ = ScanAsync();
    }
}
