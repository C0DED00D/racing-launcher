# Racing Launcher

A fullscreen arcade launcher for a home racing cabinet with **two seats** (one wheel and one screen each). Pick a
game with the wheel. The launcher sets up the emulator for each seat, starts one copy per seat, puts each window on
its own screen with that seat's controller and sound, and brings you back to the menu when you press **Guide twice**.

The main idea is **Linked** play: two copies of an arcade game on one PC, networked to each other over loopback the
way linked arcade cabinets were, each fullscreen on its own monitor with its own wheel and force feedback.

C# / .NET 10 / WPF, Windows 10/11 x64.

> **No games, ROMs, BIOS files or emulators are included.** You supply your own legally obtained game files, and
> download each emulator from its official release page.

## What it runs

| Runner | Emulator / port | Systems | Solo | Linked (2 seats, 1 PC) |
|---|---|---|---|---|
| `supermodel` | [Supermodel](https://github.com/trzy/Supermodel) | Sega Model 3; Model 2 with a newer build | ✓ | ✓ net board ring over loopback |
| `flycast` | [Flycast](https://github.com/flyinghead/flycast) | Dreamcast, NAOMI | ✓ | ✓ NAOMI network |
| `vcthunder` | [VCThunder](https://github.com/mourix/VCThunder) | Midway Vegas | ✓ | ✓ |
| `daytona-recomp` | [daytona-arcade-recomp](https://github.com/alphanu1/daytona-arcade-recomp) | native port (built from your ROM) | ✓ | ✓ (needs a small source patch, below) |
| `xenia` | [Xenia Canary netplay](https://github.com/AdrianCassar/xenia-canary) | Xbox 360 | ✓ | ✓ with a patched build (not published yet) |
| `xemu` | [xemu](https://github.com/xemu-project/xemu) | original Xbox | ✓ | – |
| `teknoparrot` | [TeknoParrot](https://github.com/teknogods/TeknoParrot) | PC-based arcade | ✓ | experimental, per-game setup |
| `exe` | any Windows program | native ports, other emulators | ✓ | – |
| `steam` | Steam (`-applaunch`) | PC games | ✓ | – |

What each runner does for you, per launch:

- writes the emulator's config for the seat: fullscreen at the seat monitor's real resolution (DPI-aware), wheel
  bindings for the seat's device, force feedback, free-running settings;
- for Linked: gives each seat its own copy/folder (saves, NVRAM, settings), network ports that point at each other,
  and the seat's controller only (so seat 1's wheel doesn't also drive seat 2);
- places and keeps each window on its seat's monitor (borderless), routes each copy's sound to the seat's audio
  device (Windows' per-app output), raises process priority for two-copy sessions;
- ends everything on **Guide twice** (or holding **Start** for 3 s) and returns to the menu.

Tiles show **SETUP NEEDED** with the reason (which emulator or file is missing) until everything is in place.

## Set up

**[CONFIG.md](CONFIG.md) is the full guide:** every config field, per-emulator setup, and how to find your own
device names. The short version:

### 1. Install and build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Emulators supermodel,flycast -BuildTools
```

This creates `config\*.json` from the examples (it never overwrites yours), builds the launcher, downloads the
emulators you name from their official GitHub releases (each checked against the SHA-256 that GitHub publishes),
builds the helper DLLs (needs Visual Studio's C++ build tools; only Linked mode uses them), and runs the setup
check. `scripts\install-emulators.ps1 -List` shows the emulators it can install.

The launcher finds its files by looking for `config\games.json` in the folders above the exe. To use another
folder, set `RACING_LAUNCHER_ROOT`.

### 2. Configure

- `scripts\list-devices.ps1` prints your controllers, audio outputs and monitors, in the form `config\seats.json`
  wants.
- `config\games.json`: your games. The example has one placeholder entry per runner with the fields it needs.
  Remove the ones you don't use.
- `config\seats.json`: each seat's monitor, wheel and audio output. `devMode: true` tiles both seats on one monitor
  while you set up.
- `art\<game id>.png` (optional): tile art, portrait 3:4 (900x1200 looks sharp on a 4K screen at 300 % scaling).
- `scripts\check-setup.ps1` reports anything missing: emulators, game files, BIOS files, devices that don't match.

### 3. Emulator settings

The one-time setup each emulator needs:

- **Supermodel:** Model 2/3 games that expect a network board even for one player work with `Network = true`,
  `SimulateNet = true` and `PortOut = PortIn` in `Config\Supermodel.ini` (the one-cabinet ring links to itself).
  NVRAM is saved only on a clean close.
- **Flycast:** NAOMI games need the NAOMI BIOS as `emulators\flycast\data\naomi.zip`.
- **xemu:** your own Xbox boot ROM, flash BIOS and HDD image in `emulators\xemu\bios\`.
- **TeknoParrot:** open `TeknoParrotUi.exe` once to accept its policies and let it fetch the components.
- **daytona-arcade-recomp:** build it from your own ROM as its README describes. For Linked, the launcher sets
  `DAYTONA_PAD_PLAYER` / `DAYTONA_JOY_MATCH` so each copy reads only its seat's controller; that needs a small patch
  to the port's `src/app/controls.cpp` that isn't published yet.

Arcade games keep coin, difficulty and link settings in their own test menus, per copy. In Linked mode each seat has
its own save under `runtime\link\<emulator>\seatN`, so set things like free play and master/slave link IDs once in
each seat's copy.

### 4. Wheels

Every game is set up for a **DirectInput force-feedback wheel**. Any retail PC wheel on its own driver works: put
part of its name or USB path in the seat's `joystick` in `seats.json`, and its button layout in the seat's `wheel`
block if it differs from the default.

**Xbox 360 Wireless Racing Wheels** work through
**[x360-wheel-ffb](https://github.com/C0DED00D/x360-wheel-ffb)**, which turns each one into a vJoy force-feedback
wheel. Clone it beside this folder, publish it where the launcher looks, and put your `wheels.json` (see its
`config\wheels.example.json`) in this folder's `config\`:

```powershell
git clone https://github.com/C0DED00D/x360-wheel-ffb ..\x360-wheel-ffb
powershell -ExecutionPolicy Bypass -File scripts\build-wheelhub.ps1
```

When `config\wheels.json` exists, the launcher starts `tools\wheelhub\WheelHub.exe` and stops it again on quit. For
each game it opens a force-feedback session (strength, inversion, rumble from that game's `ffb` entry).

### 5. Windows settings

- **Game Bar:** Settings → Gaming → Game Bar → turn off *Open Game Bar using this button on a controller*, or Guide
  opens it.
- **Firewall:** emulators that open a network port ask once. Same-PC linked play runs over loopback and doesn't need
  the rule, but the prompt takes focus until answered.

### 6. Run

```powershell
dotnet run --project src\Launcher.App -c Release
```

Log: `runtime\logs\launcher.log` (what was launched, with which arguments, and why it stopped).

## Controls

| | Wheel / gamepad | Keyboard | Mouse |
|---|---|---|---|
| Choose game | Steer, D-pad ◀ ▶, paddles | ◀ ▶ | Click a tile, or scroll |
| Choose mode | D-pad ▲ ▼ | ▲ ▼ | Click a mode |
| Start | Gas, A, Start | Enter, Space | Click the selected tile |
| Back to menu (in game) | **Guide twice**, or hold **Start** 3 s | – | – |
| Recheck game files | – | F5 | – |
| Quit / hibernate / shut down | D-pad ▲ from the top mode to QUIT, then A | Esc twice, or ▲ to QUIT + Enter | QUIT button |

The menu reads wheels through DirectInput (in the background, without exclusive access), plus XInput pads, the
keyboard and the mouse. It only acts on input while its window is in front.

## Helper DLLs (`tools\`)

Small stand-in DLLs that make two copies of an emulator behave on one PC. The runners copy them into the seat
folders when needed. Build each with its `build.cmd` from a Visual Studio developer prompt (x64 and/or x86 as
noted inside).

| Folder | What it does |
|---|---|
| `tools\xinput-inject` | `xinput1_4.dll` stand-in: shows a copy only its seat's XInput slot (`RACING_XINPUT_ONLY`), maps a DirectInput twin stick to an XInput pad (`RACING_TWINSTICK`), and merges test presses from shared memory (`scripts\xinput-press.ps1`). |
| `tools\dinput-bg` | `version.dll` stand-in for Supermodel: its DirectInput devices become background/non-exclusive, so the seat whose window isn't in front still reads its wheel. The seat's own FFB wheel stays exclusive (`DINPUT_BG_EXCLUSIVE`). |
| `tools\seat-names` | Lets a second TeknoParrot game copy run beside the first: renames TeknoParrot's fixed pipe / shared-memory names for seat 2, and moves its network binds to the seat's own address. |

Optional: `tools\sdl2\SDL2.dll` (x64) lets the Flycast runner list SDL joysticks to pick a seat's twin stick.

## Scripts

| Script | What |
|---|---|
| `scripts\setup.ps1` | One-time setup: configs from the examples, build, optional emulators and helper DLLs, setup check. |
| `scripts\install-emulators.ps1` | Emulators from their official GitHub releases, SHA-256 checked (`-List`, `-All`, `-DryRun`, `-Latest`, `-Update`). |
| `scripts\list-devices.ps1` | Controllers, audio outputs and monitors, as `seats.json` wants them. |
| `scripts\check-setup.ps1` | Checks configs, emulators, game files, seats vs devices, the wheel hub and Windows settings. |
| `scripts\build-wheelhub.ps1` | Publishes x360-wheel-ffb (cloned beside this folder) into `tools\wheelhub\`. |
| `scripts\drive.ps1` | Screenshots (`shot 1\|2`) and scan-code keys (`key`, `keys`, `focus`) for testing without someone at the cabinet. |
| `scripts\padlog.ps1` | Logs XInput slots 0 and 1 (steering, pedals, buttons) to check mappings. |
| `scripts\xinput-press.ps1`, `xinput-axis.ps1` | Fake pad presses / axes through `xinput-inject`. |
| `scripts\tp-seat-names.ps1` | Patches a TeknoParrot copy's fixed names for seat 2 (with `seat-names`). |
| `scripts\boot-select.cmd` | Starts BootSelect at logon, retrying if Windows blocks it on the first try. |

## Other programs in the solution

- **BootSelect** (`src\BootSelect`): run at logon. It waits for the primary display to settle, then starts the
  profile that matches its resolution (`config\boot-select.json`; example in `config\boot-select.example.json`).
  This lets one PC boot into the racing launcher or into something else depending on which screen is on.
- **AtGamesMouse** (`src\AtGamesMouse`): a tray app that turns two buttons of one USB controller (by VID/PID) into
  left/right mouse buttons, through Raw Input (`config\atgames-mouse.json`).

## Things worth knowing

- **4K at 300 % scaling:** code that isn't DPI-aware sees 1280x720. The launcher works in physical pixels, and the
  runners pass the real resolution.
- **Windows 11 opens Task View on every Guide press** (even with Game Bar's option off). The launcher takes focus
  back after each press. It never sends Esc for this, because some emulators quit on Esc.
- **Per-app audio routing** goes through Windows' internal AudioPolicyConfig interface (the Volume mixer's setting),
  which Windows keys by exe path. That's why linked seats run their own exe copies.
- **Two copies sharing one save file** overwrite each other's settings: every Linked seat has its own folder.
- **SDL joystick order** isn't XInput order (XInput slots come out reversed, and DirectInput devices come first),
  so runners pick devices by path or GUID, never by index.
- If the launcher is started from a sandboxed/packaged app (for example an MSIX-packaged terminal), writes to
  `%APPDATA%` can be virtualised into the package's folder, and settings made that way won't be seen when the
  launcher starts normally.

## Your own files stay out of git

The `.gitignore` is an allowlist: everything in the folder is ignored unless it's listed there, so your game files,
emulators, saves, configs (`config/*.json`) and art stay local however you lay them out. As a second guard, install
the pre-commit hook, which refuses ROM/disc-image types, binaries, files over 1 MB and the private folders:

```powershell
Copy-Item scripts\pre-commit .git\hooks\pre-commit
```

## License

MIT, see [LICENSE](LICENSE). Emulators, games and other third-party software keep their own licenses and aren't
part of this repository.
