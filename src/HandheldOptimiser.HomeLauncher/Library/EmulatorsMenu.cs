using System.Windows.Controls;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The Emulators settings: the ROM folders, and for each console found in them, which emulator plays it.
/// </summary>
internal sealed class EmulatorsMenu
{
    private readonly MenuView _view;
    private readonly MenuPresenter _menu;
    private readonly NoticePresenter _notices;
    private readonly LibraryState _state;
    private readonly ILibraryHost _host;

    // The console each row is for and its game count, so left and right can change its emulator.
    private readonly Dictionary<Button, (GameSystem System, int Games)> _systemRows = [];

    private Dictionary<string, List<EmulatorChoice>> _emulators = [];

    // Bumped each time the list is filled, so a slow, older fill does not land in a newer list.
    private int _listing;

    public EmulatorsMenu(MenuView view, MenuPresenter menu, NoticePresenter notices, LibraryState state, ILibraryHost host)
    {
        _view = view;
        _menu = menu;
        _notices = notices;
        _state = state;
        _host = host;

        view.EmulatorsGroup.Click += (_, _) => Open();
        view.AddRomFolderItem.Click += (_, _) => AddRomFolder();
        view.AddEmulatorItem.Click += (_, _) => AddEmulator();
        view.RescanEmulatorsItem.Click += (_, _) => _ = RescanAsync();
        view.EmulatorsBackItem.Click += (_, _) => menu.Back();
        menu.AddStepper(Step);
    }

    private void Open(Button? focus = null)
    {
        _menu.Armed = null;
        _menu.ClearRows(_view.EmulatorItems);
        _systemRows.Clear();
        _menu.Show(_view.EmulatorItems, "Emulators & ROMs", "Looking for consoles and emulators...", focus);
        _ = ListAsync(focus is null);
    }

    private async Task ListAsync(bool focusFirst)
    {
        var listing = ++_listing;
        var library = _state.AllTiles.Select(t => t.Game).ToList();
        var (added, folders, systems, emulators) = await Task.Run(() =>
        {
            var folders = Emulators.RomFolders();
            return (Emulators.AddedFolders(), folders, Emulators.SystemFolders(folders), Emulators.Found(library, refresh: false));
        });

        if (listing != _listing || !ReferenceEquals(_menu.List, _view.EmulatorItems))
        {
            return;
        }

        _emulators = emulators;

        // A console with games in two folders is one row.
        var consoles = systems.GroupBy(s => s.System).Select(g => (System: g.Key, Games: g.Sum(s => s.Games))).ToList();
        var games = consoles.Where(c => Emulators.ChosenFor(c.System, emulators) is not null).Sum(c => c.Games);

        _menu.Subtitle = folders.Count == 0 ? "No ROM folders yet. Add one below"
            : consoles.Count == 0 ? "No console folders found. Name each one after its console, like snes or ps2"
            : $"{games} games in the library from {consoles.Count} consoles";

        foreach (var (system, count) in consoles)
        {
            var row = _menu.AddRow(_view.EmulatorItems, _view.AddRomFolderItem, system.Name, SystemDetail(system), () => StepEmulator(system, 1));
            _systemRows[row] = (system, count);
        }

        foreach (var folder in folders)
        {
            var mine = added.Contains(folder, StringComparer.OrdinalIgnoreCase);
            var detail = mine ? "Added" : "Found";
            Button? row = null;
            row = _menu.AddRow(_view.EmulatorItems, _view.AddRomFolderItem, folder, detail, () =>
            {
                if (mine)
                {
                    ConfirmRemoveFolder(row!, folder);
                }
            });

            // Moving off a folder pressed once disarms it, and its own detail comes back.
            if (mine)
            {
                row.LostKeyboardFocus += (_, _) =>
                {
                    if (ReferenceEquals(_menu.Armed, row))
                    {
                        _menu.Armed = null;
                        row.Tag = detail;
                    }
                };
            }
        }

        if (focusFirst)
        {
            _menu.ShowItems();
        }
    }

    private string SystemDetail(GameSystem system) =>
        Emulators.ChosenFor(system, _emulators) is { } chosen ? chosen.Name : "No emulator";

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
            _notices.Show($"The emulator for {system.Name} could not be saved.");
            return;
        }

        foreach (var (row, (rowSystem, _)) in _systemRows)
        {
            if (rowSystem == system)
            {
                row.Tag = SystemDetail(system);
            }
        }

        _ = _host.ScanAsync();
    }

    /// <summary>Left and right on a console's row change its emulator.</summary>
    private bool Step(Button row, int step)
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
        if (!ReferenceEquals(_menu.Armed, row))
        {
            _menu.Armed = row;
            row.Tag = "Press again to remove";
            _notices.Show("The folder's games leave the library. Nothing is deleted.");
            return;
        }

        _menu.Armed = null;
        _notices.Show(Emulators.RemoveFolder(folder) ? $"{folder} removed." : $"{folder} could not be removed.");
        Open(_view.AddRomFolderItem);
        _ = _host.ScanAsync();
    }

    private void AddRomFolder()
    {
        var picker = new OpenFolderDialog { Title = "Add a ROM folder to the game library" };
        if (picker.ShowDialog(_host.Window) != true)
        {
            return;
        }

        _notices.Show(Emulators.AddFolder(picker.FolderName) ? $"{picker.FolderName} added." : $"{picker.FolderName} could not be added.");
        Open(_view.AddRomFolderItem);
        _ = _host.ScanAsync();
    }

    private void AddEmulator()
    {
        var picker = new OpenFileDialog
        {
            Title = "Find an emulator",
            Filter = "Emulators|*.exe",
            CheckFileExists = true
        };

        if (picker.ShowDialog(_host.Window) != true)
        {
            return;
        }

        if (!Emulators.IsKnown(picker.FileName))
        {
            _notices.Show($"The library does not know that emulator. It works with {Emulators.KnownNames}.");
            return;
        }

        _notices.Show(Emulators.AddProgram(picker.FileName) ? "Emulator added." : "The emulator could not be saved.");
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        _menu.Subtitle = "Looking for consoles and emulators...";
        await _host.ScanAsync(refreshEmulators: true);
        if (ReferenceEquals(_menu.List, _view.EmulatorItems))
        {
            Open(_view.RescanEmulatorsItem);
        }
    }
}
