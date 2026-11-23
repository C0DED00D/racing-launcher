# Setting up and configuring the launcher

This guide goes from a fresh clone to a working cabinet, then explains every config field and how to find the
values for your own hardware.

All config files are JSON with `//` comments and trailing commas allowed. Your real files
(`config/games.json`, `seats.json`, `emulators.json`, `wheels.json`, `boot-select.json`) are ignored by git, so
they never get committed. The `*.example.json` files are the templates.

## Quick start

```powershell
git clone https://github.com/C0DED00D/racing-launcher
cd racing-launcher
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Emulators supermodel,flycast -BuildTools
```

`setup.ps1` checks for the .NET 10 SDK, creates `config\*.json` from the examples (it never overwrites yours),
builds the launcher, installs the emulators you name, builds the helper DLLs, installs the git pre-commit guard, and
runs the setup check. Run it again any time; it is safe.

Then:

1. `scripts\list-devices.ps1`: shows your controllers, audio outputs and monitors. Put them in `config\seats.json`.
2. Edit `config\games.json`: one entry per game, pointing at your own files.
3. `scripts\check-setup.ps1`: reports anything missing or mismatched.
4. `dotnet run --project src\Launcher.App -c Release` (or start `src\Launcher.App\bin\Release\net10.0-windows\RacingLauncher.exe`).

| Script | What it does |
|---|---|
| `scripts\setup.ps1` | One-time setup (above). `-AllEmulators`, `-Emulators a,b`, `-BuildTools`, `-Configuration Debug`. |
| `scripts\install-emulators.ps1` | Downloads emulators from their official GitHub releases into `emulators\<name>\` and refuses any file whose SHA-256 doesn't match the digest GitHub publishes. `-List`, `-All`, `-DryRun`, `-Latest` (newest instead of the tested version), `-Update` (over an existing copy, keeping its settings files). |
| `scripts\list-devices.ps1` | Controllers (path parts for `joystick`), audio outputs (`audio`), monitors (`monitor`). `-AllAudio` also lists outputs that are off. |
| `scripts\check-setup.ps1` | Checks configs, emulators, game files, seats vs attached devices, the wheel hub, helper DLLs and the Game Bar setting. |
| `scripts\build-wheelhub.ps1` | Publishes [x360-wheel-ffb](https://github.com/C0DED00D/x360-wheel-ffb) (cloned beside this folder) into `tools\wheelhub\`. Only for Xbox 360 wheels. |

The **helper DLLs** (`-BuildTools`) are needed only for Linked play in Supermodel and Flycast. They need Visual Studio
or the free Build Tools with **Desktop development with C++**. The build scripts find it with `vswhere`; to use a
different install, set `VSDEV` to its `VsDevCmd.bat`.

## config/emulators.json

Where each emulator lives. `install-emulators.ps1` puts them where the example expects, so most people never edit
this file.

```jsonc
"emulators": {
  "flycast": { "path": "emulators/flycast/flycast.exe", "version": "2.7", "url": "https://github.com/flyinghead/flycast/releases" }
}
```

- `path`: the exe, relative to the launcher folder or absolute. If it's missing, that runner's games show
  SETUP NEEDED.
- `version`, `url`: notes for you; not read by the launcher.
- Entry names are what runners and games refer to. `supermodel-m2` is a second Supermodel install that games pick
  with `"emulator": "supermodel-m2"`. `xenia-link` is the patched Xenia build that Xenia's Linked mode needs.

## config/games.json

One entry per tile, in menu order.

| Field | Needed | Meaning |
|---|---|---|
| `id` | yes | Unique, lower case. Also the art file name: `art/<id>.png` (portrait 3:4, 900x1200 for 4K at 300 %). |
| `title`, `subtitle` | `title` | Text on the tile. |
| `runner` | yes | `supermodel` `flycast` `vcthunder` `xenia` `xemu` `teknoparrot` `daytona-recomp` `exe` `steam` |
| `rom` | most runners | The game file or folder, relative to the launcher folder or absolute. |
| `modes` | | `["Solo"]` (default) and/or `"Linked"`. Linked = one copy per seat, networked, each on its own screen. |
| `color` | | Tile colour (`"#D62828"`) when there's no art. |
| `args` | | Extra command-line arguments for the emulator, after the runner's own. |
| `emulator` | | Use this `emulators.json` entry instead of the runner's default (e.g. `supermodel-m2`). |
| `modeHints` | | `{ "Linked": "Seat 1: Create Match. Seat 2: Search." }`: shown in the menu footer for that mode. |
| `wheelRange` | | Steering rotation in degrees for this game (Logitech wheels). Default: the seat wheel's `range`. |
| `ffb` | | Wheel hub only: `{ "strength": 1.0, "invert": false, "rumble": "motors" }`. `rumble` is `"motors"` (plain rumble), `"steer"` (a steering push carried on the rumble motors) or `"off"`. |
| `controller` | | `"twinStick"`: the seat's twin stick instead of its wheel (seats.json `twinStick`). |

### Per runner

**`supermodel`** (Sega Model 3; Model 2 with `"emulator": "supermodel-m2"`)
- `rom`: the MAME-named set zip (e.g. `emulators/supermodel/ROMs/<set>.zip`). A clone set needs its parent zip in
  the same folder.
- Common `args`: `"-wide-screen"`.
- The runner writes the seat wheel's bindings into `Config\Supermodel.ini` at each launch and sets DirectInput, the
  resolution and fullscreen.
- Games that check for a network board even in single player: in `Config\Supermodel.ini` set `Network = true`,
  `SimulateNet = true` and `PortOut` = `PortIn`, so the one-cabinet ring links to itself.
- **Linked:** each seat runs from `runtime\link\supermodel[-m2]\seatN\` with its own config and NVRAM, network
  ports pointed at each other, and the `dinput-bg` helper so the window that isn't in front still reads its wheel.
  Set each seat's link ID (master/slave, or car 1/2) once, in that seat's copy of the game's test menu. NVRAM is
  saved only when Supermodel closes cleanly.

**`flycast`** (Dreamcast, NAOMI)
- `rom`: a NAOMI set zip (**keep the MAME set name**: Flycast identifies NAOMI games by it), or a Dreamcast `.gdi`,
  `.chd` or `.cdi`.
- NAOMI needs the BIOS as `emulators\flycast\data\naomi.zip`.
- `args` can override any Flycast setting: `["-config", "config:rend.WideScreen=no"]`.
- `flycastMappings`: a folder of Flycast mapping files for this game only. The game then runs from its own Flycast
  folder, so other games keep the normal wheel mappings. `config/flycast-mappings/virtual-on` is an example for twin
  sticks.
- **Linked** (NAOMI network games): one copy per seat in `runtime\link\flycast\seatN\`, seat 1 as server. Each copy
  sees only its own seat's controller, through the `xinput-inject` helper. If a game's own test menu has a link or
  communication setting, set master/slave once per seat.

**`vcthunder`** (Midway Vegas)
- `rom`: the game's CHD next to `VCThunder.exe`. VCThunder imports it into its `data\` folder.
- The game id picks VCThunder's title: ids starting with `offroad` run Offroad Thunder, anything else runs Hydro
  Thunder.
- **Linked:** per-seat exe copies and ini files; turn the network on per seat in the game's operator menu.

**`xenia`** (Xbox 360)
- `rom`: a disc image file, or the file inside an installed XBLA package (a file, not a folder).
- `profiles`: profile XUIDs to sign in at boot (Player 1 first). Create profiles in Xenia's own sign-in dialog. The
  XUID is the 16-hex-digit folder name Xenia makes for each profile.
- **Linked** needs the patched `xenia-link` build plus a local Xenia Web Services server (Node + MongoDB in
  `tools\`), which aren't published yet. Without them, Xenia games are Solo only. `linkProfiles` = two
  Live-enabled profiles, one per seat. `xeniaLinkMode: "systemlink"` uses system link instead of the local server.

**`xemu`** (original Xbox)
- `rom`: an XISO.
- Your own Xbox files go in `emulators\xemu\bios\`: `mcpx_1.0.bin`, `bios.bin`, `xbox_hdd.qcow2`.
- The runner binds port 1 to the seat's wheel and clears the HDD's cache partitions before each launch (some games
  hang loading otherwise).

**`teknoparrot`** (PC-based arcade)
- Open `TeknoParrotUi.exe` once: accept its policies, then add your game in it. That writes
  `UserProfiles\<profile>.xml`.
- `rom`: the game folder.
- `tpProfile`: that profile's name without `.xml`.
- At launch the runner rewrites the profile's controls for the seat's wheel (DirectInput).
- `windowProcess`: the game's own process name when TeknoParrot starts it as a child (used to place its window).
- **Linked** works for some games, but each needs per-game setup (seat copies, renamed pipes, network addresses), so
  treat it as experimental.

**`daytona-recomp`**
- Build [daytona-arcade-recomp](https://github.com/alphanu1/daytona-arcade-recomp) from your own ROM as its README
  describes. `rom` = the zip it was built from.
- **Linked** needs a small patch to the port's `src/app/controls.cpp` (it reads `DAYTONA_PAD_PLAYER` /
  `DAYTONA_JOY_MATCH`), which isn't published yet.

**`exe`** (any Windows program)
- `exe`: the program to start.
- `args`: its arguments.
- Solo only. Guide twice closes it.

**`steam`**
- `steamAppId`: the number in the game's store URL.
- `exe`: its exe name. The launcher waits for that process to appear, since Steam starts it.
- Set the game itself to fullscreen on seat 1's screen.
- In Steam, turn Steam Input off for the game, so Steam doesn't wrap your wheel.

## config/seats.json

A seat is one wheel plus one screen. Seat 1 is always on the primary monitor.

```jsonc
{
  "devMode": true,
  "seats": [
    { "id": 1, "xinputSlot": 0, "joystick": "col01", "monitor": "primary",   "audio": "Speakers" },
    { "id": 2, "xinputSlot": 1, "joystick": "col02", "monitor": "secondary", "audio": "HDMI" }
  ],
  "joystick": { "a": 1, "b": 2, "x": 3, "y": 4, "leftShoulder": 5, "rightShoulder": 6, "back": 7, "start": 8,
                "guide": 11, "gasAxis": "Y", "brakeAxis": "Z", "pedalsReversed": false }
}
```

| Field | Meaning | How to find it |
|---|---|---|
| `devMode` | With one monitor: two-player games tile both seats side by side on it. | `false` once you have two screens. |
| `id` | Seat number, 1 or 2. | |
| `xinputSlot` | The seat's controller number, 0-based. | 0 for seat 1, 1 for seat 2. |
| `joystick` | A part of the seat's wheel's device path that is unique to it. Games pick that seat's device by it. | `scripts\list-devices.ps1` prints a suggested path part for each controller (`vid_046d&pid_c266`, or `col01`/`col02` for the wheel hub's vJoy devices). Two identical wheels: use the part that differs. |
| `monitor` | `"primary"`, `"secondary"`, or a 0-based index. | `list-devices.ps1`. Windows' primary display = seat 1. |
| `audio` | Part of the name of the seat's audio output; that seat's game plays there. Omit for the default device. | `list-devices.ps1` (`-AllAudio` if the TV is off). TVs over HDMI usually show their brand. |
| `twinStick` | Optional: part of a twin stick's path, for games with `"controller": "twinStick"`. | `list-devices.ps1`. |
| `wheel` | Optional: this seat's wheel layout, when it differs from the shared `joystick` block. | See below. |

**Wheel layout** (`joystick` block, or a seat's own `wheel` block). Button numbers are 1-based, as Windows' *Set up
USB game controllers* (`joy.cpl`) → Properties shows them.

| Field | Meaning |
|---|---|
| `a` `b` `x` `y` `leftShoulder` `rightShoulder` `back` `start` `guide` | Which button is which. Menu: A/Start = select, B/Back = back, Guide twice = exit a game. Shoulders = paddles (gear up/down in most games). |
| `startAlso` | More buttons that also mean Start, e.g. `[7]`. |
| `gasAxis`, `brakeAxis` | `X` `Y` `Z` `Rx` `Ry` `Rz` `Slider0` `Slider1`. Steering is always X. |
| `pedalsReversed` | `true` if a pedal reads high when released. |
| `axes` | The axes the device has, in order (e.g. `["X","Y","Rz","Slider0"]`). SDL-based emulators number them this way. |
| `range` | Rotation in degrees to set at each game start (Logitech wheels). 0 = leave it. |
| `vendorId`, `productId` | The wheel's USB ids as numbers (decimal). The menu uses this layout for that device. |
| `sdlGuid`, `sdl2Guid`, `sdlName` | How SDL-based emulators see the wheel: needed by Flycast (name), daytona-recomp (SDL3 GUID) and xemu (SDL2 GUID). |

The defaults are the wheel hub's vJoy layout. For a retail wheel:

- **Buttons and pedal axes:** `joy.cpl` → the wheel → Properties shows the button numbers as you press them and
  the axes as you move them.
- **SDL name and SDL3 GUID:** x360-wheel-ffb's `WheelHub.exe --sdl-list` prints every controller SDL sees, with
  its name, ids, GUID, axis/button counts and force feedback support.
- **Guided measurement:** `WheelHub.exe --sdl-learn "<part of name>"` asks for one control at a time (its prompts
  use PlayStation-style names) and logs which button number or axis moved, with rest and pressed values.
- **SDL2 GUID:** only xemu needs it; xemu's own input settings show the device.

`twinStickLayout`: the twin sticks' button numbers (`rightUp` `rightRight` `rightDown` `rightLeft` `leftTrigger`
`leftTurbo` `rightTrigger` `rightTurbo` `start`) plus `vendorId`/`productId`, `sdl2Guid`, `sdlName`. The default
is two Sega Saturn twin sticks on a Saturn USB adapter (left lever = the hat).

## config/wheels.json (Xbox 360 wheels only)

Only needed for Xbox 360 Wireless Racing Wheels through [x360-wheel-ffb](https://github.com/C0DED00D/x360-wheel-ffb).
Copy its `config/wheels.example.json` here and see its README. When this file exists, the launcher starts the hub
from `tools\wheelhub\` and stops it on quit. Seats on the hub use `"joystick": "col01"` / `"col02"`.

## config/boot-select.json (optional)

For a PC that should boot into the launcher or into something else depending on which screen is on. BootSelect waits
for the primary display's resolution to settle, then runs the matching profile's `run` list (paths or URLs such as
`steam://rungameid/<id>`). See `config/boot-select.example.json`. To start it at logon, put a shortcut to
`scripts\boot-select.cmd` in `shell:startup`.

## config/atgames-mouse.json (optional)

For AtGamesMouse: the USB `vendorId` / `productId` (hex) of a controller, and which two of its buttons act as the
left and right mouse buttons.

## Arcade settings and saves

Arcade games keep coin, difficulty and link settings in their own test/operator menus, separately for each copy:

- Solo uses the emulator's own folder.
- Linked seat N uses `runtime\link\<emulator>\seatN\` (daytona-recomp: `%APPDATA%\daytona-recomp\daytona-linkN`).

Set free play, difficulty and the seat's link ID once in each copy, and close the emulator normally so it saves.
Killing it loses the changes. After changing link settings, restart both seats together.

## When something doesn't work

- **Tile shows SETUP NEEDED:** the reason is on the tile; `scripts\check-setup.ps1` lists all of them.
- **Logs:** `runtime\logs\launcher.log` (each launch's command line and why it ended), `runtime\logs\wheelhub.log`,
  and each emulator's own log in its folder.
- **A seat's game reads the wrong wheel, or none:** its `joystick` text must match exactly one device. The launcher
  log says "no controller matches ... (N attached: ...)" and lists what it saw.
- **Guide opens Game Bar:** Settings → Gaming → Game Bar → turn off *Open Game Bar using this button on a
  controller*.
- **Windows Firewall prompt:** emulators that open a network port ask once. Same-PC linking runs over loopback and
  doesn't need the rule, but the prompt steals focus until it's answered.
- **A freshly built exe or DLL is blocked** (Smart App Control / Code Integrity): Windows sometimes blocks a brand-new
  unsigned build for a moment; starting it again usually works. `scripts\boot-select.cmd` retries for that reason.
- **Settings made from a sandboxed/packaged app don't stick:** apps installed as MSIX packages can have their
  `%APPDATA%` writes redirected into the package's folder, so the emulator never sees them. Make emulator settings
  from a normal desktop session.
