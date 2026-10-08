using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Adding programs the stores do not list: picked from the Start menu with the controller, or with the
/// file picker by touch or mouse.
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
        OpenSettingsGroup(AddItems, "Add a program");
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

        MenuStore.Text = programs.Count == 0 ? "No other programs in the Start menu" : "Or pick one from the Start menu";

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
            Title = "Add a program to the game library",
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

        if (!File.Exists(path) || AddedPrograms.Add(path, title) is not { } game)
        {
            StatusText.Text = $"{title ?? Path.GetFileNameWithoutExtension(path)} could not be added.";
            return;
        }

        // The new tile takes the focus once the scan has found it.
        _focusedKey = game.Key;
        StatusText.Text = $"{game.Title} added to the library.";
        _ = ScanAsync();
    }
}
