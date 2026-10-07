using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Emulators settings: the ROM folders, and for each console found in them, which emulator plays it.
/// </summary>
public partial class LibraryWindow
{
    // The console each row is for and its game count, so left and right can change its emulator.
    private readonly Dictionary<Button, (GameSystem System, int Games)> _systemRows = [];

    // A row's own detail line, put back when a press to confirm is abandoned.
    private readonly Dictionary<Button, string> _confirmDetails = [];

    private Dictionary<string, List<EmulatorChoice>> _emulators = [];

    private void OnOpenEmulators(object sender, RoutedEventArgs e) => OpenEmulators();

    private void OpenEmulators(Button? focus = null)
    {
        HideMenuLists();
        _menu = EmulatorItems;
        _confirming = null;
        MenuTitle.Text = "Emulators";
        MenuStore.Text = "Looking for consoles and emulators...";
        ClearRows(EmulatorItems);
        _systemRows.Clear();
        _confirmDetails.Clear();
        ShowMenuItems(focus);
        _ = ListEmulatorsAsync(focus is null);
    }

    private async Task ListEmulatorsAsync(bool focusFirst)
    {
        var listing = ++_listing;
        var library = _allTiles.Select(t => t.Game).ToList();
        var (added, folders, systems, emulators) = await Task.Run(() =>
        {
            var folders = Emulators.RomFolders();
            return (Emulators.AddedFolders(), folders, Emulators.SystemFolders(folders), Emulators.Found(library, refresh: false));
        });

        if (listing != _listing || !ReferenceEquals(_menu, EmulatorItems))
        {
            return;
        }

        _emulators = emulators;

        // A console with games in two folders is one row.
        var consoles = systems.GroupBy(s => s.System).Select(g => (System: g.Key, Games: g.Sum(s => s.Games))).ToList();
        var games = consoles.Where(c => Emulators.ChosenFor(c.System, emulators) is not null).Sum(c => c.Games);

        MenuStore.Text = folders.Count == 0 ? "No ROM folders yet. Add one below"
            : consoles.Count == 0 ? "No console folders found. Name them snes, ps2, gc and so on"
            : $"{games} games in the library from {consoles.Count} consoles";

        foreach (var (system, count) in consoles)
        {
            var row = AddRow(EmulatorItems, AddRomFolderItem, system.Name, SystemDetail(system, count), () => StepEmulator(system, 1));
            _systemRows[row] = (system, count);
        }

        foreach (var folder in folders)
        {
            var mine = added.Contains(folder, StringComparer.OrdinalIgnoreCase);
            var detail = mine ? "Added by you. A removes it" : "Found automatically";
            Button? row = null;
            row = AddRow(EmulatorItems, AddRomFolderItem, folder, detail, () =>
            {
                if (mine)
                {
                    ConfirmRemoveFolder(row!, folder);
                }
            });

            if (mine)
            {
                _confirmDetails[row] = detail;
                row.LostKeyboardFocus += OnConfirmRowLostFocus;
            }
        }

        if (focusFirst)
        {
            ShowMenuItems();
        }
    }

    private string SystemDetail(GameSystem system, int games)
    {
        var count = games == 1 ? "1 game" : $"{games} games";
        var choices = _emulators.GetValueOrDefault(system.Id) ?? [];
        return Emulators.ChosenFor(system, _emulators) is not { } chosen
            ? $"No emulator found for its {count}. Find an emulator below"
            : $"{chosen.Name}. {count}" + (choices.Count > 1 ? ". Left and right pick another emulator" : string.Empty);
    }

    /// <summary>Moves a console on to its next emulator, and the library's games with it.</summary>
    private void StepEmulator(GameSystem system, int step)
    {
        var choices = _emulators.GetValueOrDefault(system.Id) ?? [];
        if (choices.Count < 2)
        {
            return;
        }

        var current = Emulators.ChosenFor(system, _emulators);
        var index = current is null ? 0 : choices.IndexOf(current);
        var next = choices[(index + step + choices.Count) % choices.Count];
        if (!Emulators.Choose(system, next))
        {
            StatusText.Text = $"The emulator for {system.Name} could not be saved.";
            return;
        }

        foreach (var (row, (rowSystem, games)) in _systemRows)
        {
            if (rowSystem == system)
            {
                row.Tag = SystemDetail(system, games);
            }
        }

        _ = ScanAsync();
    }

    private bool StepEmulatorSetting(Button row, int step)
    {
        if (!_systemRows.TryGetValue(row, out var system))
        {
            return false;
        }

        StepEmulator(system.System, step);
        return true;
    }

    private void ConfirmRemoveFolder(Button row, string folder)
    {
        if (!ReferenceEquals(_confirming, row))
        {
            _confirming = row;
            row.Tag = "Press again to remove it. Its games leave the library, nothing is deleted";
            return;
        }

        _confirming = null;
        StatusText.Text = Emulators.RemoveFolder(folder) ? $"{folder} removed." : $"{folder} could not be removed.";
        OpenEmulators(AddRomFolderItem);
        _ = ScanAsync();
    }

    private void OnAddRomFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Add a ROM folder to the game library" };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        StatusText.Text = Emulators.AddFolder(picker.FolderName) ? $"{picker.FolderName} added." : $"{picker.FolderName} could not be added.";
        OpenEmulators(AddRomFolderItem);
        _ = ScanAsync();
    }

    private void OnAddEmulator(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Find an emulator",
            Filter = "Emulators|*.exe",
            CheckFileExists = true
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        if (!Emulators.IsKnown(picker.FileName))
        {
            StatusText.Text = $"That is not an emulator the library knows. It works with {Emulators.KnownNames}.";
            return;
        }

        StatusText.Text = Emulators.AddProgram(picker.FileName) ? "Emulator added." : "The emulator could not be saved.";
        _ = RescanEmulatorsAsync();
    }

    private void OnRescanEmulators(object sender, RoutedEventArgs e) => _ = RescanEmulatorsAsync();

    private async Task RescanEmulatorsAsync()
    {
        MenuStore.Text = "Looking for consoles and emulators...";
        await ScanAsync(refreshEmulators: true);
        if (ReferenceEquals(_menu, EmulatorItems))
        {
            OpenEmulators(RescanEmulatorsItem);
        }
    }
}
