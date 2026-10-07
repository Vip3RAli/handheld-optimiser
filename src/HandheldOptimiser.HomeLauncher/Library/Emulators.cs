using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>A console the library can list games for.</summary>
/// <param name="Folders">Folder names that hold its games, as EmuDeck, ES-DE and RetroBat name them.</param>
/// <param name="Extensions">Its game file types, the preferred ones first.</param>
/// <param name="Cores">RetroArch cores that play it, the best first, without the "_libretro.dll".</param>
internal sealed record GameSystem(string Id, string Name, string[] Folders, string[] Extensions, string[] Cores);

/// <summary>A standalone emulator.</summary>
/// <param name="Arguments">Its command line, with {rom} for the game file. Each starts the game full screen
/// and quits back to the library when the game is closed, where the emulator allows it.</param>
internal sealed record EmulatorApp(string Id, string Name, string[] ExeNames, string Arguments, string[] Systems);

/// <summary>An emulator found on this device, and the system it plays.</summary>
/// <param name="Id">"retroarch:core" or the standalone emulator's id, which is how the player's choice is stored.</param>
internal sealed record EmulatorChoice(string Id, string Name, string Exe, string Arguments);

/// <summary>
/// Games in ROM folders, played in an emulator. The player's ROM folders hold a folder per system named
/// as EmuDeck, ES-DE and RetroBat name them (snes, ps2, gc ...), or the folder is itself one of those.
/// Each system plays in the first emulator found for it, which the player can change: standalone
/// emulators first, then RetroArch with the best core it has.
/// </summary>
internal static partial class Emulators
{
    private const string FoldersKey = Program.SettingsKey + @"\Emulators\RomFolders";
    private const string PathsKey = Program.SettingsKey + @"\Emulators\Programs";
    private const string ChoiceKey = Program.SettingsKey + @"\Emulators\Choice";
    private const string KeyPrefix = "rom:";

    // A folder with more than this many game files is probably not a ROM folder, or not one to list.
    private const int MaxGamesPerSystem = 5000;

    // How deep below a system folder games are looked for, for games kept in a folder each.
    private const int SearchDepth = 2;

    public static readonly GameSystem[] Systems =
    [
        new("nes", "NES", ["nes", "famicom", "fc"], [".nes", ".fds", ".unf", ".zip", ".7z"], ["mesen", "nestopia", "fceumm"]),
        new("snes", "Super Nintendo", ["snes", "sfc", "superfamicom", "snesna"], [".sfc", ".smc", ".zip", ".7z"], ["snes9x", "bsnes"]),
        new("n64", "Nintendo 64", ["n64"], [".z64", ".n64", ".v64", ".zip", ".7z"], ["mupen64plus_next", "parallel_n64"]),
        new("gb", "Game Boy", ["gb"], [".gb", ".zip", ".7z"], ["gambatte", "sameboy", "mgba"]),
        new("gbc", "Game Boy Color", ["gbc"], [".gbc", ".zip", ".7z"], ["gambatte", "sameboy", "mgba"]),
        new("gba", "Game Boy Advance", ["gba"], [".gba", ".zip", ".7z"], ["mgba", "vbam"]),
        new("nds", "Nintendo DS", ["nds"], [".nds", ".zip", ".7z"], ["melondsds", "melonds", "desmume"]),
        new("3ds", "Nintendo 3DS", ["3ds", "n3ds"], [".3ds", ".cci", ".cxi", ".3dsx"], ["citra"]),
        new("gc", "GameCube", ["gc", "gamecube", "ngc"], [".rvz", ".iso", ".gcm", ".gcz", ".ciso"], ["dolphin"]),
        new("wii", "Wii", ["wii"], [".rvz", ".wbfs", ".iso", ".gcz", ".wad"], ["dolphin"]),
        new("wiiu", "Wii U", ["wiiu"], [".wua", ".wux", ".wud", ".rpx"], []),
        new("switch", "Switch", ["switch", "nsw"], [".nsp", ".xci", ".nro"], []),
        new("genesis", "Mega Drive", ["genesis", "megadrive", "md"], [".md", ".gen", ".smd", ".bin", ".zip", ".7z"], ["genesis_plus_gx", "picodrive"]),
        new("mastersystem", "Master System", ["mastersystem", "sms"], [".sms", ".zip", ".7z"], ["genesis_plus_gx", "picodrive"]),
        new("gamegear", "Game Gear", ["gamegear", "gg"], [".gg", ".zip", ".7z"], ["genesis_plus_gx"]),
        new("segacd", "Mega CD", ["segacd", "megacd"], [".m3u", ".chd", ".cue", ".iso"], ["genesis_plus_gx", "picodrive"]),
        new("sega32x", "32X", ["sega32x", "32x"], [".32x", ".zip", ".7z"], ["picodrive"]),
        new("saturn", "Saturn", ["saturn"], [".m3u", ".chd", ".cue", ".iso"], ["mednafen_saturn", "yabasanshiro"]),
        new("dreamcast", "Dreamcast", ["dreamcast", "dc"], [".m3u", ".chd", ".gdi", ".cdi", ".cue"], ["flycast"]),
        new("psx", "PlayStation", ["psx", "ps1", "playstation"], [".m3u", ".chd", ".pbp", ".cue", ".iso"], ["swanstation", "mednafen_psx_hw", "pcsx_rearmed"]),
        new("ps2", "PlayStation 2", ["ps2"], [".chd", ".iso", ".cso", ".zso", ".gz"], ["pcsx2"]),
        new("psp", "PSP", ["psp"], [".iso", ".cso", ".chd", ".pbp"], ["ppsspp"]),
        new("xbox", "Xbox", ["xbox"], [".iso"], []),
        new("pcengine", "PC Engine", ["pcengine", "tg16", "pce"], [".pce", ".m3u", ".chd", ".cue", ".zip", ".7z"], ["mednafen_pce_fast", "mednafen_pce"]),
        new("ngp", "Neo Geo Pocket", ["ngp", "ngpc"], [".ngp", ".ngc", ".zip", ".7z"], ["mednafen_ngp"]),
        new("wonderswan", "WonderSwan", ["wonderswan", "wswan", "wonderswancolor", "wswanc"], [".ws", ".wsc", ".zip", ".7z"], ["mednafen_wswan"]),
        new("atari2600", "Atari 2600", ["atari2600"], [".a26", ".bin", ".zip", ".7z"], ["stella"]),
        new("lynx", "Atari Lynx", ["atarilynx", "lynx"], [".lnx", ".zip", ".7z"], ["handy", "mednafen_lynx"]),
        new("virtualboy", "Virtual Boy", ["virtualboy"], [".vb", ".zip", ".7z"], ["mednafen_vb"]),
        new("neogeo", "Neo Geo", ["neogeo"], [".zip", ".7z"], ["fbneo"]),
        new("arcade", "Arcade", ["arcade", "fbneo", "mame"], [".zip", ".7z"], ["fbneo", "mame2003_plus", "mame"])
    ];

    public static readonly EmulatorApp[] Standalone =
    [
        new("dolphin", "Dolphin", ["Dolphin.exe"], "-b -e \"{rom}\"", ["gc", "wii"]),
        new("pcsx2", "PCSX2", ["pcsx2-qt.exe", "pcsx2-qtx64.exe", "pcsx2-qtx64-avx2.exe"], "-batch -fullscreen \"{rom}\"", ["ps2"]),
        new("duckstation", "DuckStation", ["duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt.exe"], "-batch -fullscreen \"{rom}\"", ["psx"]),
        new("ppsspp", "PPSSPP", ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"], "--fullscreen \"{rom}\"", ["psp"]),
        new("cemu", "Cemu", ["Cemu.exe"], "-f -g \"{rom}\"", ["wiiu"]),
        new("ryujinx", "Ryujinx", ["Ryujinx.exe"], "--fullscreen \"{rom}\"", ["switch"]),
        new("citron", "Citron", ["citron.exe"], "-f -g \"{rom}\"", ["switch"]),
        new("eden", "Eden", ["eden.exe"], "-f -g \"{rom}\"", ["switch"]),
        new("sudachi", "Sudachi", ["sudachi.exe"], "-f -g \"{rom}\"", ["switch"]),
        new("melonds", "melonDS", ["melonDS.exe"], "-f \"{rom}\"", ["nds"]),
        new("azahar", "Azahar", ["azahar.exe"], "-f \"{rom}\"", ["3ds"]),
        new("lime3ds", "Lime3DS", ["lime3ds.exe", "lime3ds-gui.exe"], "-f \"{rom}\"", ["3ds"]),
        new("citra", "Citra", ["citra-qt.exe"], "-f \"{rom}\"", ["3ds"]),
        new("flycast", "Flycast", ["flycast.exe"], "\"{rom}\"", ["dreamcast"]),
        new("xemu", "xemu", ["xemu.exe"], "-full-screen -dvd_path \"{rom}\"", ["xbox"])
    ];

    private const string RetroArchExe = "retroarch.exe";

    // Folder names an emulator is often installed under, so Program Files is not searched in full.
    private static readonly string[] EmulatorFolderWords =
        ["retroarch", "dolphin", "pcsx2", "duckstation", "ppsspp", "cemu", "ryujinx", "citron", "eden", "sudachi",
         "melonds", "azahar", "lime3ds", "citra", "flycast", "xemu", "emulator", "emudeck", "emulation", "retrobat"];

    // Region, revision and dump tags in game file names: "(USA)", "[!]", "(Rev 1)".
    [GeneratedRegex(@"\s*(\([^)]*\)|\[[^\]]*\])")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    public static GameSystem? SystemFor(string folderName) =>
        Systems.FirstOrDefault(s => s.Folders.Contains(folderName, StringComparer.OrdinalIgnoreCase));

    /// <summary>The ROM folders the player added.</summary>
    public static List<string> AddedFolders() => ReadList(FoldersKey);

    /// <summary>
    /// ROM folders in the usual places, scanned without being added: a ROMs folder at the root of any
    /// drive or in the user's folder, and EmuDeck's and RetroBat's own.
    /// </summary>
    public static List<string> FoundFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(profile, "ROMs"),
            Path.Combine(profile, "Emulation", "roms"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ROMs")
        };

        foreach (var drive in ReadyDrives())
        {
            candidates.Add(Path.Combine(drive, "ROMs"));
            candidates.Add(Path.Combine(drive, "Emulation", "roms"));
            candidates.Add(Path.Combine(drive, "RetroBat", "roms"));
        }

        return candidates.Where(SafeExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folders scanned: the ones the player added and the ones found.</summary>
    public static List<string> RomFolders() => AddedFolders().Concat(FoundFolders()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether an exe is one of the emulators the library knows how to start games in.</summary>
    public static bool IsKnown(string exe) =>
        Standalone.Any(a => a.ExeNames.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))
        || Path.GetFileName(exe).Equals(RetroArchExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The emulators the library knows, for telling the player which ones it can use.</summary>
    public static string KnownNames => string.Join(", ", Standalone.Select(a => a.Name).Distinct().Prepend("RetroArch"));

    /// <returns>False when the folder could not be stored.</returns>
    public static bool AddFolder(string folder) => AddToList(FoldersKey, folder);

    /// <returns>False when the folder could not be removed.</returns>
    public static bool RemoveFolder(string folder) => RemoveFromList(FoldersKey, folder);

    /// <returns>False when the emulator could not be stored.</returns>
    public static bool AddProgram(string exe) => AddToList(PathsKey, exe);

    /// <summary>Each system found in the folders, with how many games it has.</summary>
    public static List<(GameSystem System, string Folder, int Games)> SystemFolders(IEnumerable<string> romFolders)
    {
        var found = new List<(GameSystem, string, int)>();
        foreach (var root in romFolders)
        {
            foreach (var (system, folder) in SystemsIn(root))
            {
                found.Add((system, folder, RomFiles(system, folder).Count()));
            }
        }

        return found;
    }

    // The emulators found last time. Looking takes a moment, so it is only done again when asked.
    private static readonly object FoundLock = new();
    private static Dictionary<string, List<EmulatorChoice>>? _found;

    /// <summary>The emulators found for each system, looked for again when <paramref name="refresh"/> is set.</summary>
    public static Dictionary<string, List<EmulatorChoice>> Found(IEnumerable<Game> library, bool refresh)
    {
        lock (FoundLock)
        {
            if (!refresh && _found is { } found)
            {
                return found;
            }
        }

        var fresh = FindEmulators(library);
        lock (FoundLock)
        {
            _found = fresh;
        }

        return fresh;
    }

    /// <summary>The emulators found by the last scan, or none before the first.</summary>
    public static Dictionary<string, List<EmulatorChoice>> LastFound()
    {
        lock (FoundLock)
        {
            return _found ?? [];
        }
    }

    /// <summary>Every emulator found for each system, in the order they are offered.</summary>
    private static Dictionary<string, List<EmulatorChoice>> FindEmulators(IEnumerable<Game> library)
    {
        var exes = FindExes(library);
        var choices = new Dictionary<string, List<EmulatorChoice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in Systems)
        {
            var list = new List<EmulatorChoice>();
            foreach (var app in Standalone.Where(a => a.Systems.Contains(system.Id)))
            {
                if (app.ExeNames.Select(name => exes.GetValueOrDefault(name)).FirstOrDefault(p => p is not null) is { } exe)
                {
                    list.Add(new EmulatorChoice(app.Id, app.Name, exe, app.Arguments));
                }
            }

            if (exes.GetValueOrDefault(RetroArchExe) is { } retroArch)
            {
                var cores = Path.Combine(Path.GetDirectoryName(retroArch)!, "cores");
                foreach (var core in system.Cores)
                {
                    var dll = Path.Combine(cores, $"{core}_libretro.dll");
                    if (File.Exists(dll))
                    {
                        list.Add(new EmulatorChoice($"retroarch:{core}", $"RetroArch ({core.Replace('_', ' ')})", retroArch,
                            $"-f -L \"{dll}\" \"{{rom}}\""));
                    }
                }
            }

            choices[system.Id] = list;
        }

        return choices;
    }

    /// <summary>The emulator a system plays in: the player's choice if it is still there, or else the first found.</summary>
    public static EmulatorChoice? ChosenFor(GameSystem system, IReadOnlyDictionary<string, List<EmulatorChoice>> emulators)
    {
        var found = emulators.GetValueOrDefault(system.Id) ?? [];
        var chosen = Choice(system);
        return found.FirstOrDefault(e => e.Id == chosen) ?? found.FirstOrDefault();
    }

    /// <returns>False when the choice could not be stored.</returns>
    public static bool Choose(GameSystem system, EmulatorChoice emulator)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ChoiceKey);
            key.SetValue(system.Id, emulator.Id, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save the emulator for {system.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>A library entry for every game in the ROM folders whose system has an emulator.</summary>
    public static IEnumerable<Game> Scan(IReadOnlyDictionary<string, List<EmulatorChoice>> emulators)
    {
        foreach (var root in RomFolders())
        {
            foreach (var (system, folder) in SystemsIn(root))
            {
                if (ChosenFor(system, emulators) is not { } emulator)
                {
                    continue;
                }

                var files = RomFiles(system, folder).ToList();

                // Two dumps of one game, such as two regions, keep their tags so they can be told apart.
                var titles = files.GroupBy(f => TitleOf(f), StringComparer.CurrentCultureIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

                foreach (var file in files)
                {
                    var title = TitleOf(file);
                    yield return new Game(
                        Key: KeyPrefix + Hash(file),
                        Title: titles.Contains(title) ? Path.GetFileNameWithoutExtension(file) : title,
                        Store: GameStore.Emulator,
                        CoverPath: null,
                        IconPath: emulator.Exe,
                        LaunchTarget: emulator.Exe,
                        LaunchArguments: emulator.Arguments.Replace("{rom}", file),
                        WorkingDirectory: Path.GetDirectoryName(emulator.Exe),
                        InstallDirectory: Path.GetDirectoryName(file),
                        ExecutablePath: emulator.Exe,
                        Platform: system.Name);
                }
            }
        }
    }

    /// <summary>A game's name from its file: "Super Metroid (Japan, USA) (En,Ja).sfc" is "Super Metroid".</summary>
    public static string TitleOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var title = Spaces().Replace(Tags().Replace(name, string.Empty), " ").Trim();
        return title.Length > 0 ? title : name;
    }

    /// <summary>The system folders in a ROM folder, or the folder itself when it is one.</summary>
    private static IEnumerable<(GameSystem System, string Folder)> SystemsIn(string root)
    {
        if (!SafeExists(root))
        {
            yield break;
        }

        if (SystemFor(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))) is { } own)
        {
            yield return (own, root);
            yield break;
        }

        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (SystemFor(Path.GetFileName(folder)) is { } system)
            {
                yield return (system, folder);
            }
        }
    }

    /// <summary>
    /// The system's game files in a folder and the folders below it. A playlist (.m3u) stands for the discs
    /// it lists, and a .cue or .gdi for its tracks, so a game is listed once however many files it has.
    /// </summary>
    public static IEnumerable<string> RomFiles(GameSystem system, string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = SearchDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", options)
                .Where(f => system.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Take(MaxGamesPerSystem)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // Discs a playlist or cue sheet already covers, and files a cue sheet names as its tracks.
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".m3u" or ".cue" or ".gdi"))
        {
            covered.UnionWith(ListedFiles(list));
        }

        return files.Where(f => !covered.Contains(f)).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>The files a playlist, cue sheet or .gdi refers to, as full paths.</summary>
    private static IEnumerable<string> ListedFiles(string list)
    {
        var folder = Path.GetDirectoryName(list)!;
        var listed = new List<string>();
        try
        {
            foreach (var raw in File.ReadLines(list).Take(200))
            {
                var line = raw.Trim();
                string? name = null;
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (Path.GetExtension(list).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
                {
                    name = line;
                }
                else if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) && line.IndexOf('"') is var start and >= 0
                    && line.IndexOf('"', start + 1) is var end and > 0)
                {
                    name = line[(start + 1)..end];
                }
                else if (Path.GetExtension(list).Equals(".gdi", StringComparison.OrdinalIgnoreCase))
                {
                    // Track lines: number, start, type, sector size, file name, offset.
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    name = parts.Length >= 5 ? parts[4].Trim('"') : null;
                }

                if (name is not null && name.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                {
                    listed.Add(Path.GetFullPath(Path.Combine(folder, name)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Nothing is hidden behind a list that cannot be read.
        }

        return listed;
    }

    /// <summary>
    /// The emulator exes on this device, by file name: ones the player pointed to, ones in the library or
    /// the Start menu, and ones in the folders emulators are usually installed in.
    /// </summary>
    private static Dictionary<string, string> FindExes(IEnumerable<Game> library)
    {
        var wanted = Standalone.SelectMany(a => a.ExeNames).Append(RetroArchExe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path)
        {
            if (path is not null && wanted.Contains(Path.GetFileName(path)) && !found.ContainsKey(Path.GetFileName(path)) && SafeFileExists(path))
            {
                found[Path.GetFileName(path)] = path;
            }
        }

        foreach (var path in ReadList(PathsKey))
        {
            Consider(path);
        }

        // RetroArch from Steam, and emulators added as programs, are in the library already.
        foreach (var game in library.Where(g => g.Store != GameStore.Emulator))
        {
            Consider(game.ExecutablePath);
            if (game.InstallDirectory is { } dir && EmulatorFolderWords.Any(w => game.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var name in wanted)
                {
                    Consider(Path.Combine(dir, name));
                }
            }
        }

        foreach (var (_, _, target) in AddedPrograms.StartMenuPrograms([]))
        {
            Consider(target);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(local, "Programs"),
            Path.Combine(appData, "EmuDeck", "Emulators"),
            appData,
            Path.Combine(profile, "scoop", "apps"),
            Path.Combine(profile, "Emulators"),
            Path.Combine(profile, "Emulation"),
            profile
        };

        foreach (var drive in ReadyDrives())
        {
            places.Add(drive);
            places.Add(Path.Combine(drive, "Emulators"));
            places.Add(Path.Combine(drive, "Emulation"));
            places.Add(Path.Combine(drive, "RetroBat", "emulators"));
        }

        foreach (var place in places.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SearchPlace(place, wanted, Consider);
        }

        return found;
    }

    /// <summary>
    /// Looks in the emulator-named folders of a place, two levels down, so a whole drive or Program Files
    /// is never walked: "Dolphin-x64\Dolphin.exe" or "scoop\apps\pcsx2\current\pcsx2-qt.exe".
    /// </summary>
    private static void SearchPlace(string place, HashSet<string> wanted, Action<string> consider)
    {
        IEnumerable<string> folders;
        try
        {
            if (!Directory.Exists(place))
            {
                return;
            }

            folders = Directory.EnumerateDirectories(place)
                .Where(f => EmulatorFolderWords.Any(w => Path.GetFileName(f).Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 2,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var folder in folders)
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", options))
                {
                    if (wanted.Contains(Path.GetFileName(exe)))
                    {
                        consider(exe);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next folder, then.
            }
        }
    }

    private static string? Choice(GameSystem system)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ChoiceKey);
            return key?.GetValue(system.Id) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadyDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return drives.Where(d =>
        {
            try
            {
                return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
                return false;
            }
        }).Select(d => d.RootDirectory.FullName).ToList();
    }

    private static bool SafeExists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16].ToLowerInvariant();

    // Lists are a key of numbered values, each holding one path.
    private static List<string> ReadList(string keyName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyName);
            return (key?.GetValueNames() ?? [])
                .Select(name => key!.GetValue(name) as string)
                .OfType<string>()
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool AddToList(string keyName, string path)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyName);
            if (key.GetValueNames().Any(name => string.Equals(key.GetValue(name) as string, path, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            key.SetValue(Hash(path), path, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not save {path}: {ex.Message}");
            return false;
        }
    }

    private static bool RemoveFromList(string keyName, string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyName, writable: true);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (string.Equals(key!.GetValue(name) as string, path, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Program.Log($"Could not remove {path}: {ex.Message}");
            return false;
        }
    }
}
