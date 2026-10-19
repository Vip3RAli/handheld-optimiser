<p align="center">
  <img src="src/HandheldOptimiser/Assets/app-64.png" width="64" height="64" alt="Handheld Optimiser icon">
</p>

<h1 align="center">Handheld Optimiser</h1>

<p align="center">
  A Windows tuning app for the ASUS ROG Ally and other Windows handhelds.<br>
  It applies the tweaks handheld owners usually make by hand, adds a console-style game library for full screen mode, and can undo every change.
</p>

<p align="center">
  <a href="https://github.com/Vip3RAli/handheld-optimiser/releases/latest"><b>Download the latest release</b></a>
</p>

<p align="center">
  <img src="docs/dashboard-cards.png" alt="Handheld Optimiser dashboard with the Apply now button, how many recommended tweaks are applied, hardware readings and a card for each of eight goals" width="900">
</p>

---

## Built to be undone

- A System Restore point is made before anything changes. If Windows cannot make one, nothing is changed.
- Every tweak records the setting it replaced, so you can undo it on its own, or undo everything at once with **Undo all changes** on the Maintenance & backup page, even after a restart or a Windows update.
- The app will not change Windows Update, Microsoft Defender, the firewall, graphics and audio drivers or Game Pass. It leaves your handheld's own software alone too, apart from the ASUS services you can choose to replace on the ROG Ally (see below).
- Anything that lowers security or turns off a feature says so, and is listed in the confirmation before it runs.

## Install

1. Download **HandheldOptimiser-Setup-&lt;version&gt;.exe** from the [latest release](https://github.com/Vip3RAli/handheld-optimiser/releases/latest).
2. Run it. Windows may show "Windows protected your PC", because the installer is not code-signed yet. Choose **More info**, then **Run anyway**.
3. Accept the administrator prompt. The app changes system settings, so Windows asks each time it opens.

.NET is bundled. New versions install over the old one and keep your undo history.

- Requirements: Windows 10 (2004) or Windows 11, 64-bit.
- Full screen mode needs Windows 11 24H2 or later on a device with the Xbox full screen experience.
- Designed and tested on the ROG Ally. The ROG Xbox Ally, Legion Go, MSI Claw, Steam Deck (running Windows), AYANEO, GPD and OneXPlayer are recognised but have not been tested.

## What it does

Windows is set up for a desktop PC, not a 7-inch handheld. Handheld Optimiser makes the changes handheld owners usually make by hand, in one app you can use by touch or with the controller.

The Dashboard has **Apply now**, which turns on the recommended tweaks in one go, a count of how many are on, hardware readings, and a card for each of eight goals. Tap a card to open its page, and Back to return.

| Card | On its page |
|---|---|
| **Performance** | Memory Integrity and VBS, game and CPU priority, GPU scheduling, network throttling |
| **Battery & sleep** | Hibernate after a short sleep, what can wake the handheld, Fast Startup |
| **Clean up** | Privacy, ads and assistants, background activity and services |
| **Apps** | Startup programs and preinstalled apps |
| **Full screen** | The home app, signing in to full screen mode, replacing Armoury Crate, touch and desktop tweaks |
| **Maintenance & backup** | Restore point and undo, one-off jobs, storage tweaks |
| **Health** | What Windows reports about the device |
| **Updates** | Windows, Store and winget updates, and game runtimes |

Each tweak is a tile with a switch. Tap the tile to read what it does. A tweak with a warning asks you to confirm before it turns on.

Apply now includes turning off Memory Integrity. That can raise frame rates in CPU-heavy games, but it lowers protection against malicious drivers. The confirmation lists every trade-off like this before anything runs.

The other pages:

- Apps removes preinstalled apps such as TikTok, Candy Crush and Copilot. Nothing is selected until you choose it.
- Maintenance & backup has the one-off jobs: reset network connections, clear shader caches, clear old update files and crash dumps, and Compact OS.
- Battery & sleep can hibernate the handheld after a short sleep or at a press of the power button, so it uses almost no power in a bag.
- Health checks that your handheld's own services are running, and can turn back on any that another tool turned off. It also shows what Windows reports about sleep (battery used while asleep, and what can wake the handheld or keep it awake), the processor settings in each power plan, free space and device encryption.
- Updates lists Windows, Store and winget updates together and installs the ones you tick. Driver updates are off unless you switch them on. Games, the BIOS and firmware are not listed.

### Replacing Armoury Crate on the ROG Ally

**Replace ASUS Services with Handheld Library and widget**, on the Full screen page, turns off Armoury Crate and about a dozen ASUS background services. Handheld Optimiser then runs the Armoury Crate and Command Center buttons and the controller mode itself, through the game library and its Game Bar widget. It uses about 16 MB of memory, where ASUS's services use about 480 MB. ASUS's drivers stay installed. BIOS and controller updates come through Armoury Crate, so switch this off to update, then on again.

## Full screen mode and the game library

Windows 11's full screen experience normally opens the Xbox app. Handheld Optimiser can open its own game library there, or Steam Big Picture, Armoury Crate SE or any other launcher.

<p align="center">
  <img src="docs/game-library.jpg" alt="The game library with the Games, Apps and Not installed tabs, a row of game covers and the selected game's artwork as the background" width="900">
</p>

<p align="center">
  <img src="docs/game-library-old-school.jpg" alt="The game library's Old School layout: a list of categories above a stack of large staggered game cards, like the Xbox 360 dashboard" width="900">
</p>

1. Open **Full screen** and choose **Handheld Optimiser library** or **Another launcher**.
2. Switch on **Open your chosen app in full screen mode**.
3. If you like, switch on **Enter full screen mode at sign-in** to start straight into it like a console.

If the chosen app cannot start, the Xbox app opens instead. **Handheld Optimiser Game Library** in the Start menu opens the library without changing your home app.

The library finds installed games from **Steam**, **Xbox / PC Game Pass**, **Epic Games**, **Battle.net**, **GOG**, the **EA App** and **Ubisoft Connect**, plus programs you add and games in your ROM folders.

| Feature | What it does |
|---|---|
| **Layouts** | A grid, a row like a console's home screen, or **Old School**: big staggered cards under a list of categories. Choose one under **Settings > Library display > What it shows**. |
| **Tabs** | Games, Favourites, one per store, Apps and Not installed. **LB / RB** move along them and on to the settings and power buttons. |
| **Artwork** | Wide artwork, covers and icons from SteamGridDB with your own free key, for games outside Steam and for apps such as Chrome. |
| **Quick actions** (**X**) | Favourite or hide a game, set its game profile, launch arguments or artwork. |
| **Game profiles** | A power mode, resolution, refresh rate, brightness, TDP Engine frame rate and fan curve per game, put back when it closes. |
| **TDP Engine** | Holds a game at 30, 40, 45 or 60 fps on the lowest power limit that manages it, and lowers the limit when the processor reaches your temperature limit. Each game starts from the limit it settled on last time. Switch it on under **Settings > Sleep and clean-up > Play and sleep**, or per game in its profile. |
| **Fan curves** | Quiet, Cool or Max fans while a game runs, with or without TDP Engine, and the Ally's own curve back when it closes. Every curve reaches full speed by 90 °C. Dynamic moves between the three, stepping up early when the power jumps and waiting 30 seconds before stepping down. Set it next to TDP Engine, or per game. |
| **Game Bar widget** | TDP Engine in Xbox Game Bar (Win + G): see the watts, frame rate and temperature, and change the frame rate or fan curve mid-game. Started from Game Bar with nothing running, it follows the game you're playing and stops a minute after it closes. |
| **Last played** | Your last game as a wide card above the grid, with its play time, size and profile beside it, and its description from Steam's store or Wikipedia if you switch that on. |
| **Quick Resume** | The home button pauses the game behind the library, and **A** carries on where you left off. Games with anti-cheat are never paused. |
| **Play and sleep** | Close background programs and store apps around a game, and go back to your game after sleep. |
| **Docked mode** | A layout and zoom of its own on a TV or monitor. |
| **Not installed** | Games you own on Steam, Epic and GOG, ready to install, read from each store's app on your device. Your own free Steam Web API key gives Steam's exact list. |
| **Game deals** | An optional row with the Epic Games Store's free games of the week, which needs no key, and games on sale from IsThereAnyDeal with your own free API key. |
| **Emulators** | Games from your ROM folders, including EmuDeck's and RetroBat's, in the emulator found for each console. |
| **Clean up** | Clear temporary files, shader caches and the Recycle Bin, and switch startup apps. |
| **Power** (**View**) | Sleep, hibernate, restart or shut down. |

TDP Engine and the fan curves work on the ROG Ally only.

Everything works with the controller or by touch. **Menu** opens the settings, and **Up** from the top row reaches the settings and power buttons.

The library uses about 25 MB of memory while a game runs. Steam Big Picture uses about 1.4 GB while it is open.

### Privacy

The library only uses the internet to check for a newer release, and for the features you switch on:

- artwork from [SteamGridDB](https://www.steamgriddb.com), which is sent the names of your apps and of games outside Steam
- your Steam game list and wishlist, and store covers for games you have not installed
- deals from [IsThereAnyDeal](https://isthereanydeal.com) and the Epic Games Store's free games
- your last game's details from Steam's store or [Wikipedia](https://www.wikipedia.org)

Most of these use your own keys, which stay on your device.

### Controller tips

Keep the controller in **Gamepad** mode for the library. The main app reads the controller as arrow keys, so on the ROG Ally set the Control mode to **Desktop** while you use it. If the home button opens Steam, turn off Steam's guide button setting under **Steam > Settings > Controller**.

## Updating

The app checks for a newer release when it starts, and the library does the same. **Update now** downloads the installer, checks its signature, installs it and reopens. Your tweaks and undo history are kept. Every release is signed with the developer's key, and the app refuses anything whose signature does not match.

## Uninstalling

Uninstall from **Settings > Apps > Installed apps**. This removes the app but **does not undo tweaks**. If you want Windows put back as it was, use **Undo all changes** on the Maintenance & backup page first. Your undo history is kept, so if you reinstall later you can still undo everything.

## FAQ

**Why does Windows ask for administrator permission every time?**
Almost every change the app makes is machine-wide. The game library is separate and never asks.

**What does the TDP Engine service do?**
Counting a game's frames needs administrator, which the library never asks for, so the installer adds a small Windows service, **Handheld Optimiser TDP Engine**. It only runs while a game started from the library has TDP Engine or a fan curve on, or while the Game Bar widget is holding a game, and when the game closes it puts Armoury Crate's operating mode back, with its own power limits and fan curve. While Armoury Crate is replaced, it also runs the Ally's Armoury Crate and Command Center buttons and its controller mode. Besides the library, only the Game Bar widget's own app package may talk to it. Every change it makes is logged in `%ProgramData%\HandheldOptimiser\logs\auto-tdp.log`.

**Why does Windows warn that the installer is unrecognised?**
The installer is not signed with a paid code-signing certificate yet. Each release lists the installer's SHA-256 hash if you want to verify your download.

**Something went wrong. What do I do?**
Use **Undo all changes** on the Maintenance & backup page, or roll back with the System Restore point the app created. Logs are in `%LOCALAPPDATA%\HandheldOptimiser\logs`; attach them to an issue.

**Full screen mode opens to a blank grey screen.**
This affected 0.4.0 and earlier. Open **Full screen** and switch on **Open your chosen app in full screen mode** once to fix it.

## Building from source

Requires Windows, the .NET 10 SDK and Inno Setup 6.

```powershell
winget install --id JRSoftware.InnoSetup -e --scope user
.\installer\build-installer.ps1
```

This writes `artifacts\HandheldOptimiser-Setup-<version>.exe`. The version comes from `<Version>` in `src\HandheldOptimiser\HandheldOptimiser.csproj`. Run the tests with `dotnet test`; they never touch the real registry, services or packages.

If the update signing key is present, the script also writes a `.sig` file. Upload both files to the release, or the in-app updater will not install it. The key is managed with `tools\UpdateSigner` (`keygen`, `export-backup`, `import-backup`) and never goes in the repository.

| Folder | Contents |
|---|---|
| `src/HandheldOptimiser` | The WPF app: tweak definitions, the tweak engine and the protected list |
| `src/HandheldOptimiser.HomeLauncher` | The launcher and game library Windows starts as the full screen home app |
| `installer` | Inno Setup script, build script and the home app registration package |
| `tests/HandheldOptimiser.Tests` | xUnit tests for undo, the protected lists and the library |
| `tools/UpdateSigner` | Creates the update signing key and signs installers; never shipped |

## Credits

- Full screen home app approach learned from [AnyFSE](https://github.com/ashpynov/AnyFSE) by Artem Shpynov (MIT).
- UI built with [WPF-UI](https://github.com/lepoco/wpfui) and the [CommunityToolkit MVVM](https://github.com/CommunityToolkit/dotnet) library.
