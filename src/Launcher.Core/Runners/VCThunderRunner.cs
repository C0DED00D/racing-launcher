using System.Diagnostics;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Hydro Thunder (and Offroad Thunder) through VCThunder, a native host for the original arcade code.
/// Games are imported from the user's MAME CHD into data\&lt;title&gt;\ by VCThunder itself.
/// Phase 1 handles Solo only. Linked mode will use --link local with a --save/--log per seat.
/// </summary>
public sealed class VCThunderRunner : IRunner
{
    public string Id => "vcthunder";

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var exe = config.EmulatorPath(Id);
        if (exe == null) return ["VCThunder isn't set up in config/emulators.json."];
        if (!File.Exists(exe)) return [$"VCThunder not found at {exe}"];

        var title = Title(game);
        var imported = Path.Combine(Path.GetDirectoryName(exe)!, "data", title);
        if (!Directory.Exists(imported) || !Directory.EnumerateFiles(imported, "*.EXE").Any())
            return [$"{game.Title} isn't imported yet: put its .chd next to VCThunder.exe and run VCThunder once."];
        return [];
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Split)
            throw new NotSupportedException($"{game.Title} has no split screen; use Linked.");

        var exe = config.EmulatorPath(Id)!;
        var dir = Path.GetDirectoryName(exe)!;

        if (mode == PlayMode.Linked) return BuildLinked(game, seats, config, dir);

        // VCThunder has no fullscreen flag; its ini setting is a borderless window over the monitor.
        if (IniFile.Set(Path.Combine(dir, "vcthunder.ini"), "graphics", "fullscreen", "true"))
            Log.Info("  vcthunder.ini: set [graphics] fullscreen = true");
        UseWheel(Path.Combine(dir, "vcthunder.ini"), seats[0], seats[0].Wheel, pin: false);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Title(game));
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);

        var seat = seats[0];
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: false, seat.Audio)]);
    }

    /// <summary>
    /// One copy per seat from runtime/link/vcthunder/seatN: its own copy of the program (VCThunder reads
    /// vcthunder.ini next to its exe), its own ini (that seat's wheel, the shared game data) and its own save,
    /// which holds the operator settings Network Enabled = ON and Unit ID = seat number (set once in F2).
    /// </summary>
    LaunchPlan BuildLinked(GameProfile game, IReadOnlyList<SeatAssignment> seats, LauncherConfig config, string home)
    {
        var instances = new List<InstancePlan>();
        foreach (var seat in seats)
        {
            var dir = Path.Combine(config.Root, "runtime", "link", "vcthunder", $"seat{seat.SeatId}");
            Directory.CreateDirectory(Path.Combine(dir, "save"));
            foreach (var f in new[] { "VCThunder.exe", "VCThunder.dll", "vcglide.dll", "SDL3.dll" })
            {
                var src = Path.Combine(home, f); var dst = Path.Combine(dir, f);
                if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) != File.GetLastWriteTimeUtc(dst)) File.Copy(src, dst, true);
            }
            foreach (var s in Directory.GetFiles(Path.Combine(home, "save")))
            {
                var dst = Path.Combine(dir, "save", Path.GetFileName(s));
                if (!File.Exists(dst)) File.Copy(s, dst);
            }
            var ini = Path.Combine(dir, "vcthunder.ini");
            File.Copy(Path.Combine(home, "vcthunder.ini"), ini, true);
            IniFile.Set(ini, "paths", "data_dir", Path.Combine(home, "data"));
            IniFile.Set(ini, "graphics", "fullscreen", "true");
            UseWheel(ini, seat, seat.Wheel, pin: true);

            var psi = new ProcessStartInfo(Path.Combine(dir, "VCThunder.exe")) { WorkingDirectory = dir, UseShellExecute = false };
            psi.ArgumentList.Add(Title(game));
            psi.ArgumentList.Add("--link");
            psi.ArgumentList.Add("local");
            foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
            // Its fullscreen follows the monitor its window is on, so move each window onto its seat's monitor.
            instances.Add(new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: true, seat.Audio));
        }
        return new LaunchPlan(game, PlayMode.Linked, instances);
    }

    /// <summary>
    /// The DirectInput force-feedback wheel (vJoy now, a native wheel later). VCThunder opens the first DirectInput
    /// device that publishes FFB effects and plays the game's own wheel motor on it; with two (seat 2), pin=true names
    /// the seat's device ([analog] dinput_name = instance GUID of the seats.json "joystick" device; Solo names it too
    /// when it is found). Hydro also needs its own wheel force setting in the F2 menu.
    /// </summary>
    static void UseWheel(string ini, SeatAssignment seat, JoystickLayout j, bool pin)
    {
        static string Axis(string a) => JoystickLayout.NormAxis(a).ToLowerInvariant(); // x y z rx ry rz slider0 slider1
        IniFile.Set(ini, "analog", "provider", "dinput");
        IniFile.Set(ini, "analog", "dinput_steering_axis", "x");
        IniFile.Set(ini, "analog", "dinput_throttle_axis", Axis(j.GasAxis));
        IniFile.Set(ini, "analog", "dinput_brake_axis", Axis(j.BrakeAxis));
        IniFile.Set(ini, "analog", "dinput_pedal_mode", "separate");
        IniFile.Set(ini, "analog", "dinput_throttle_invert", j.PedalsReversed ? "true" : "false");
        IniFile.Set(ini, "analog", "dinput_brake_invert", j.PedalsReversed ? "true" : "false");
        // dinput_name matches VCThunder's "device id" (vcthunder.log: "<product name> product_<guid>
        // instance_<guid>"), not the device path, so the seat's wheel is named by its DirectInput instance GUID.
        Input.DirectInputDevice? dev = null;
        if (Input.DirectInputDevices.UsableMatch(seat.JoystickMatch, $"Seat {seat.SeatId}") is { } m || !pin)
            try { dev = Input.DirectInputDevices.Find(seat.JoystickMatch); } catch (Exception ex) { Log.Warn($"  DirectInput: {ex.Message}"); }
        IniFile.Set(ini, "analog", "dinput_name", dev == null ? "" : $"instance_{dev.Instance}");
        if (dev != null) Log.Info($"  Seat {seat.SeatId}: VCThunder wheel = {dev.InstanceName} ({dev.Path})");
        // Buttons count from 1 on a DirectInput controller: boost on both paddles, coin on Back, Start on every
        // Start button (the G923: Options and R2). Action names from VCThunder.dll: coin1, start1, boost.
        IniFile.Set(ini, "hydro.gamepad", "boost", $"{j.LeftShoulder},{j.RightShoulder}");
        IniFile.Set(ini, "hydro.gamepad", "coin", j.Back.ToString());
        IniFile.Set(ini, "hydro.gamepad", "coin1", j.Back.ToString());
        IniFile.Set(ini, "hydro.gamepad", "start1", string.Join(",", j.StartButtons));
    }

    /// <summary>VCThunder's title id: "hydro" or "offroad". Taken from the game id.</summary>
    static string Title(GameProfile game) => game.Id.StartsWith("offroad", StringComparison.OrdinalIgnoreCase) ? "offroad" : "hydro";
}
