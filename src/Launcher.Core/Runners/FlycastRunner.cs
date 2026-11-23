using System.Diagnostics;
using Launcher.Core.Config;
using Launcher.Core.Display;

namespace Launcher.Core.Runners;

/// <summary>
/// Dreamcast and NAOMI games in Flycast (Crazy Taxi arcade, Crazy Taxi 2, Virtual-On Oratorio Tangram 5.66).
/// Flycast keeps emu.cfg, mappings\ and data\ (BIOS, saves) next to its exe. Everything this runner needs is
/// passed as transient -config values, so emu.cfg is never relied on:
///   - fullscreen on the seat's monitor (the window opens there, then goes desktop-fullscreen),
///   - internal resolution = the monitor's height (2160 on the cabinet's 4K screens),
///   - widescreen rendering plus Flycast's per-game widescreen hacks (Crazy Taxi 2 has one; the arcade
///     Crazy Taxi gets ours from flycast.lua),
///   - Solo: every wheel on player 1, since Flycast numbers SDL joysticks in its own order, not by XInput slot.
/// Wheel buttons are in mappings\SDL_Xbox 360 Wireless Controller[_arcade].cfg (SDL XInput driver, see below).
/// Arcade Crazy Taxi's DRIVE/REVERSE gears are the JVS joystick up/down (btn_dpad1_up/down).
/// NAOMI games need data\naomi.zip (the BIOS).
///
/// Games with their own mappings (games.json "flycastMappings") run from their own copy of Flycast
/// (runtime/flycast/&lt;id&gt; Solo, runtime/link/flycast/seatN Linked), so their gamepad mappings don't replace
/// the wheel mappings above.
///
/// Linked (NAOMI only): one copy per seat on Flycast's NAOMI network. Seat 1 is the server (LocalPort 37391), seat 2
/// connects to 127.0.0.1:37391 from its own LocalPort. The master waits about 20 s for the slave.
/// Flycast's own master/slave EEPROM write did not stick for Virtual-On 5.66 (its test menu still said NO LINK);
/// it links once each seat's GAME ASSIGNMENTS > COMMUNICATION MODE is set (seat 1 MASTER, seat 2 SLAVE), which is
/// saved in that seat's data\&lt;set&gt;.zip.nvmem on a clean close.
///
/// Controllers in Linked mode: SDL numbers its joysticks in its own order (XInput slots in reverse, plus any
/// DirectInput/HID device such as vJoy first), so a joystick number can't be mapped to a seat. Instead each seat's
/// folder gets our stand-in xinput1_4.dll (tools/xinput-inject) with RACING_XINPUT_ONLY = the seat's slot: that copy
/// sees only its own XInput controller, DirectInput and HIDAPI are off, and every joystick it sees is player 1.
/// Tested 2026-11-07 with virtual pads: each seat followed only its own pad, and a linked versus match played.
/// </summary>
public sealed class FlycastRunner : IRunner
{
    public string Id => "flycast";

    const int NetworkPort = 37391;

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(Id);
        if (exe == null) problems.Add("Flycast isn't set up in config/emulators.json.");
        else if (!File.Exists(exe)) problems.Add($"Flycast not found at {exe}");

        if (string.IsNullOrWhiteSpace(game.Rom)) problems.Add("No ROM set in config/games.json.");
        else
        {
            var rom = config.Resolve(game.Rom);
            if (!File.Exists(rom)) problems.Add($"ROM not found at {rom}");
            else if (exe != null && IsArcade(rom) && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "data", "naomi.zip")))
                problems.Add("NAOMI BIOS missing: put naomi.zip in emulators/flycast/data.");
        }
        if (game.FlycastMappings != null && !Directory.Exists(config.Resolve(game.FlycastMappings)))
            problems.Add($"Mapping folder not found at {config.Resolve(game.FlycastMappings)}");
        return problems;
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Split || (mode == PlayMode.Linked && !game.Modes.Contains(PlayMode.Linked)))
            throw new NotSupportedException($"{game.Title} is single player only in Flycast.");
        var rom = config.Resolve(game.Rom!);
        if (mode == PlayMode.Linked && !IsArcade(rom))
            throw new NotSupportedException($"{game.Title}: Flycast links NAOMI games only.");

        var exe = config.EmulatorPath(Id)!;
        var home = Path.GetDirectoryName(exe)!;
        int count = mode == PlayMode.Solo ? 1 : seats.Count;
        var instances = new List<InstancePlan>();
        for (int i = 0; i < count; i++)
        {
            var seat = seats[i];
            var a = seat.Area;
            string seatExe = exe;
            if (mode == PlayMode.Linked || game.FlycastMappings != null)
            {
                var dir = mode == PlayMode.Linked
                    ? Path.Combine(config.Root, "runtime", "link", "flycast", $"seat{seat.SeatId}")
                    : Path.Combine(config.Root, "runtime", "flycast", game.Id);
                seatExe = PrepareFolder(config, home, dir, game);
                if (mode == PlayMode.Linked && !game.UsesTwinStick) InstallSeatXInput(config, dir);
            }

            // Fullscreen covers the whole monitor, so a seat sharing one (dev mode) runs windowed in its half.
            bool fullscreen = a == ScreenRect.Of(seat.Monitor);
            var settings = new List<string>
            {
                // SDL goes desktop-fullscreen on the monitor the window opens on, so open it inside the seat's area.
                $"window:fullscreen={(fullscreen ? "yes" : "no")}",
                "window:maximized=no",
                $"window:left={a.X + (fullscreen ? 100 : 0)}",
                $"window:top={a.Y + (fullscreen ? 100 : 0)}",
                $"window:width={(fullscreen ? 640 : a.Width)}",
                $"window:height={(fullscreen ? 360 : a.Height)}",
                $"config:rend.Resolution={Math.Max(480, a.Height)}",
                "config:rend.WideScreen=yes",
                "config:rend.WidescreenGameHacks=yes",
                "config:ForceFreePlay=yes",
            };
            // Twin-stick games, and Solo wheel games: only the seat's own controller plays (port A), every other
            // joystick is off (-1). Solo needs it since 2026-11-20: seat 1 is the G923, and vJoy device 1 (no
            // longer fed by the hub) would otherwise also play, resting at half throttle. Not found: every
            // joystick plays player 1, as before (a Linked wheel copy only sees its own seat's controller).
            int? stickId = game.UsesTwinStick || mode == PlayMode.Solo ? SeatJoystickId(config, seat) : null;
            for (int j = 0; j < 8; j++)
                settings.Add($"input:maple_sdl_joystick_{j}={(stickId is int s && s != j ? -1 : 0)}");

            if (mode == PlayMode.Linked)
            {
                settings.Add("network:Enable=yes");
                settings.Add("network:EnableUPnP=no");
                settings.Add($"network:ActAsServer={(i == 0 ? "yes" : "no")}");
                settings.Add($"network:LocalPort={NetworkPort + i}");
                if (i > 0) settings.Add($"network:server=127.0.0.1:{NetworkPort}");
            }
            else settings.Add("network:Enable=no");

            var psi = new ProcessStartInfo(seatExe)
            {
                WorkingDirectory = Path.GetDirectoryName(seatExe)!,
                UseShellExecute = false,
            };
            // Through SDL's default RawInput driver the 360 wheel shows only 3 axes (no separate pedals) and its own
            // button order. The XInput driver gives the normal layout: axes 0 LX, 1 LY, 2 LT, 3 RX, 4 RY, 5 RT,
            // buttons 0 A 1 B 2 X 3 Y 4 LB 5 RB 6 Back 7 Start 10 Guide, hat 0. The mapping files are written for it
            // (device name "Xbox 360 Wireless Controller").
            psi.Environment["SDL_JOYSTICK_RAWINPUT"] = "0";
            psi.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
            if (mode == PlayMode.Linked && !game.UsesTwinStick)
            {
                // Only this seat's XInput slot (stand-in DLL in the seat folder), no other joystick drivers.
                psi.Environment["RACING_XINPUT_ONLY"] = seat.XInputSlot.ToString();
                psi.Environment["SDL_DIRECTINPUT_ENABLED"] = "0";
                psi.Environment["SDL_JOYSTICK_HIDAPI"] = "0";
                psi.Environment["SDL_JOYSTICK_WGI"] = "0";
            }
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(string.Join(",", settings));
            foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(rom);

            // Fullscreen: Flycast places itself; moving its window would fight that. Windowed: place it borderless.
            instances.Add(new InstancePlan($"Seat {seat.SeatId}", psi, a, PlaceWindow: !fullscreen, seat.Audio,
                // The server must be listening before the client connects.
                StartDelay: i > 0 ? TimeSpan.FromSeconds(2) : default));
        }
        return new LaunchPlan(game, mode, instances);
    }

    /// <summary>
    /// A Flycast folder of its own: a copy of the exe (Windows keeps the per-app audio output per exe path), the BIOS,
    /// the main install's mappings with the game's own on top, and its own saves in data\. flycast.lua is left out:
    /// it patches Crazy Taxi's memory.
    /// </summary>
    static string PrepareFolder(LauncherConfig config, string home, string dir, GameProfile game)
    {
        var data = Path.Combine(dir, "data");
        var mappings = Path.Combine(dir, "mappings");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(mappings);

        var exe = SupermodelRunner.CopyIfNewer(Path.Combine(home, "flycast.exe"), Path.Combine(dir, "flycast.exe"));
        var cfg = Path.Combine(dir, "emu.cfg");
        if (!File.Exists(cfg) && File.Exists(Path.Combine(home, "emu.cfg"))) File.Copy(Path.Combine(home, "emu.cfg"), cfg);
        var bios = Path.Combine(home, "data", "naomi.zip");
        if (File.Exists(bios)) SupermodelRunner.CopyIfNewer(bios, Path.Combine(data, "naomi.zip"));

        // Flycast rewrites mapping files on first use, so they are copied again every launch.
        foreach (var f in Directory.GetFiles(Path.Combine(home, "mappings"), "*.cfg"))
            File.Copy(f, Path.Combine(mappings, Path.GetFileName(f)), true);
        if (game.FlycastMappings != null)
            foreach (var f in Directory.GetFiles(config.Resolve(game.FlycastMappings), "*.cfg"))
                File.Copy(f, Path.Combine(mappings, Path.GetFileName(f)), true);
        if (game.UsesTwinStick) WriteTwinStickMapping(mappings, config.Seats.TwinStickLayout);
        return exe;
    }

    /// <summary>
    /// NAOMI mapping for the twin stick (SDL buttons are 0-based, hat 0 = codes 256 up 257 down 258 left 259 right),
    /// in Virtual-On 5.66's layout (vonot_inputs): left lever = joystick 1, right lever = Y (up) / Z (down) and
    /// joystick 2 left/right, left trigger A, left turbo B, right trigger C, right turbo X, Start.
    /// </summary>
    static void WriteTwinStickMapping(string mappings, TwinStickLayout l)
    {
        int B(int n) => n - 1;
        var binds = new (int Code, string Action)[]
        {
            (256, "btn_dpad1_up"), (257, "btn_dpad1_down"), (258, "btn_dpad1_left"), (259, "btn_dpad1_right"),
            (B(l.RightUp), "btn_y"), (B(l.RightDown), "btn_z"), (B(l.RightLeft), "btn_dpad2_left"), (B(l.RightRight), "btn_dpad2_right"),
            (B(l.LeftTrigger), "btn_a"), (B(l.LeftTurbo), "btn_b"), (B(l.RightTrigger), "btn_c"), (B(l.RightTurbo), "btn_x"),
            (B(l.Start), "btn_start"),
        };
        var text = "[digital]\n" + string.Concat(binds.Select((b, i) => $"bind{i} = {b.Code}:{b.Action}\n")) +
                   "\n[emulator]\ndead_zone = 10\nmapping_name = Twin stick (Virtual-On, racing launcher)\n" +
                   "rumble_power = 0\nsaturation = 100\nversion = 4\n";
        File.WriteAllText(Path.Combine(mappings, $"SDL_{l.SdlName}_arcade.cfg"), text);
    }

    /// <summary>
    /// Copies the stand-in xinput1_4.dll (x64 build of tools/xinput-inject) next to the seat's flycast.exe, where
    /// Windows loads it instead of the system one. It forwards to the system DLL and, with RACING_XINPUT_ONLY set,
    /// hides every other slot. Without it both copies would see both seats' controllers.
    /// </summary>
    static void InstallSeatXInput(LauncherConfig config, string dir)
    {
        var dll = Path.Combine(config.Root, "tools", "xinput-inject", "x64", "xinput1_4.dll");
        if (File.Exists(dll)) SupermodelRunner.CopyIfNewer(dll, Path.Combine(dir, "xinput1_4.dll"));
        else Log.Warn($"  Flycast seat controllers: {dll} missing; each copy will see every controller");
    }

    /// <summary>
    /// The SDL instance id Flycast will give the seat's twin stick (seats.json seat "twinStick", matched against the
    /// SDL device path), found by listing joysticks with our SDL2 under the same hints. The twin sticks are
    /// DirectInput devices (Saturn adapter), so the XInput stand-in DLL that Linked uses for pads can't pick them.
    /// Null (with a warning) if it isn't attached: then every joystick plays, as before.
    /// </summary>
    static int? SeatJoystickId(LauncherConfig config, SeatAssignment seat)
    {
        try
        {
            var all = Input.SdlJoysticks.List(config.Resolve(@"tools\sdl2\SDL2.dll"),
                new Dictionary<string, string> { ["SDL_JOYSTICK_RAWINPUT"] = "0" });
            var hit = string.IsNullOrWhiteSpace(seat.JoystickMatch) ? null
                : all.FirstOrDefault(j => j.Path.Contains(seat.JoystickMatch, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                Log.Info($"  Seat {seat.SeatId}: Flycast controller = SDL joystick {hit.InstanceId} {hit.Name} ({hit.Path})");
                return hit.InstanceId;
            }
            Log.Warn($"  Seat {seat.SeatId}: no controller matches \"{seat.JoystickMatch}\" (SDL sees: " +
                     string.Join("; ", all.Select(j => $"{j.InstanceId} {j.Name} {j.Path}")) + ")");
        }
        catch (Exception ex) { Log.Warn($"  Seat {seat.SeatId}: listing SDL joysticks failed: {ex.Message}"); }
        return null;
    }

    /// <summary>NAOMI/Atomiswave games are MAME-style zips; Dreamcast games are disc images.</summary>
    static bool IsArcade(string rom) =>
        rom.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || rom.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
}
