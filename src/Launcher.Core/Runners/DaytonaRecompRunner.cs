using System.Diagnostics;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Daytona USA through daytona-arcade-recomp (native port, no emulator).
/// The game is compiled from the ROM set, so the exe only exists after a build with the ROM present.
/// Solo uses the port's default profile. Linked runs one copy per seat with --profile linkN: its own settings
/// folder (%APPDATA%\daytona-recomp\daytona-linkN, seeded from the default one), a two-cabinet TCP ring on
/// localhost, and DAYTONA_PAD_PLAYER = the seat's XInput slot (our patch in src/app/controls.cpp).
/// Controls: the DirectInput wheel (vJoy) via joystick bindings, written into each profile on every launch; each copy
/// sees only its seat's wheel (DAYTONA_JOY_MATCH, second patch).
/// MASTER/SLAVE and the car number are the game's own test-menu settings, saved in each profile's EEPROM.
/// </summary>
public sealed class DaytonaRecompRunner : IRunner
{
    public string Id => "daytona-recomp";

    const int BasePort = 15112;

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(game.Rom)) problems.Add("No ROM set in config/games.json.");
        else if (!File.Exists(config.Resolve(game.Rom))) problems.Add($"ROM not found at {config.Resolve(game.Rom)}");

        var exe = config.EmulatorPath(Id);
        if (exe == null) problems.Add("daytona-recomp isn't set up in config/emulators.json.");
        else if (!File.Exists(exe))
            problems.Add("The Daytona port hasn't been built with your ROM yet (see README: rebuild Daytona USA).");
        return problems;
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Split)
            throw new NotSupportedException("Daytona USA has no split screen; use Linked.");

        var exe = config.EmulatorPath(Id)!;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "daytona-recomp");
        if (mode == PlayMode.Solo)
        {
            WriteIni(Path.Combine(root, "daytona", "launcher.ini"), WheelBindings(seats[0].Wheel));
            var psi = Start(exe, game, config);
            UseSeatWheel(psi, seats[0]);
            psi.ArgumentList.Add("--fullscreen"); // borderless fullscreen on the primary monitor
            // The game makes itself fullscreen; moving its window would fight that, so only bring it forward.
            return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seats[0].SeatId}", psi, seats[0].Area, PlaceWindow: false, seats[0].Audio)]);
        }

        var instances = new List<InstancePlan>();
        for (int i = 0; i < seats.Count; i++)
        {
            var seat = seats[i];
            var profile = $"link{seat.SeatId}";
            PrepareProfile(profile, BasePort + i, $"127.0.0.1:{BasePort + (i + 1) % seats.Count}", seat.Wheel);

            // Its own copy of the exe (it needs nothing beside it): Windows keeps the per-app audio output per exe path.
            var seatDir = Path.Combine(config.Root, "runtime", "link", "daytona", $"seat{seat.SeatId}");
            Directory.CreateDirectory(seatDir);
            var seatExe = SupermodelRunner.CopyIfNewer(exe, Path.Combine(seatDir, Path.GetFileName(exe)));
            var psi = Start(seatExe, game, config);
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(profile);
            psi.Environment["DAYTONA_PAD_PLAYER"] = seat.XInputSlot.ToString();
            UseSeatWheel(psi, seat);
            // Windowed (the port can't pick a monitor); the launcher makes it borderless over the seat's monitor.
            instances.Add(new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: true, seat.Audio));
        }
        return new LaunchPlan(game, mode, instances);
    }

    /// <summary>
    /// This copy sees only the seat's own wheel (DAYTONA_JOY_MATCH, our patch in src/app/controls.cpp: both vJoy
    /// devices have the same SDL GUID, so the bindings alone can't tell them apart), and reads it in the background
    /// (SDL drops input for unfocused windows otherwise; both seats need theirs all the time).
    /// </summary>
    static void UseSeatWheel(ProcessStartInfo psi, SeatAssignment seat)
    {
        if (Input.DirectInputDevices.UsableMatch(seat.JoystickMatch, $"Seat {seat.SeatId}") is { } m) psi.Environment["DAYTONA_JOY_MATCH"] = m;
        psi.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
    }

    /// <summary>
    /// Every control on the DirectInput wheel (seats.json "joystick"). Bound by SDL GUID ("joystick" + sdlGuid); the
    /// game's force feedback goes to the device steering is bound to, so this also gives it the full drive board.
    /// SDL numbers buttons from 0 (Windows from 1) and axes in the device's order (JoystickLayout.SdlAxis); vJoy
    /// pedals rest at -32768, reversed ones (G923) at 32767. One joystick input per control, so Start is
    /// JoystickLayout.OneStart (the G923's R2).
    /// </summary>
    static IEnumerable<(string Key, string Value)> WheelBindings(JoystickLayout j)
    {
        var g = j.SdlGuid;
        string Btn(int n) => n >= 1 ? $"button:{g}:{n - 1}" : "none";
        string Pedal(string a) => j.PedalsReversed ? $"axis:{g}:{j.SdlAxis(a)}:32767:-32768" : $"axis:{g}:{j.SdlAxis(a)}:-32768:32767";
        int steer = j.SdlAxis("X");
        return
        [
            ("steer_left.joy", $"axis:{g}:{steer}:0:-32768"), ("steer_right.joy", $"axis:{g}:{steer}:0:32767"),
            ("accelerate.joy", Pedal(j.GasAxis)), ("brake.joy", Pedal(j.BrakeAxis)),
            ("gear_up.joy", Btn(j.RightShoulder)), ("gear_down.joy", Btn(j.LeftShoulder)),
            ("view1.joy", Btn(j.A)), ("view2.joy", Btn(j.B)), ("view3.joy", Btn(j.X)), ("view4.joy", Btn(j.Y)),
            ("coin.joy", Btn(j.Back)), ("start.joy", Btn(j.OneStart)),
        ];
    }

    static void WriteIni(string ini, IEnumerable<(string Key, string Value)> values)
    {
        if (!File.Exists(ini)) return; // the port writes it on first run; bindings are added the next launch
        var lines = File.ReadAllLines(ini).ToList();
        foreach (var (key, value) in values)
        {
            int i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            if (i >= 0) lines[i] = $"{key}={value}"; else lines.Add($"{key}={value}");
        }
        File.WriteAllLines(ini, lines);
    }

    static ProcessStartInfo Start(string exe, GameProfile game, LauncherConfig config)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add("--rom");
        psi.ArgumentList.Add(config.Resolve(game.Rom!));
        psi.ArgumentList.Add("--autostart");
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Seeds %APPDATA%\daytona-recomp\daytona-&lt;profile&gt; from the default profile once (settings, EEPROM, backup
    /// RAM), then sets this seat's link settings and windowed mode in its launcher.ini on every launch.
    /// </summary>
    static void PrepareProfile(string profile, int port, string next, JoystickLayout wheel)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "daytona-recomp");
        var solo = Path.Combine(root, "daytona");
        var dir = Path.Combine(root, $"daytona-{profile}");
        Directory.CreateDirectory(dir);
        if (Directory.Exists(solo))
            foreach (var f in Directory.GetFiles(solo))
            {
                var dst = Path.Combine(dir, Path.GetFileName(f));
                if (!File.Exists(dst) && !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) File.Copy(f, dst);
            }

        var ini = Path.Combine(dir, "launcher.ini");
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : [];
        void Set(string key, string value)
        {
            int i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            if (i >= 0) lines[i] = $"{key}={value}"; else lines.Add($"{key}={value}");
        }
        Set("link", "1");
        Set("link_port", port.ToString());
        Set("link_next", next);
        Set("fullscreen", "0");
        Set("skip_launcher", "1");
        foreach (var (k, v) in WheelBindings(wheel)) Set(k, v);
        File.WriteAllLines(ini, lines);
    }
}
