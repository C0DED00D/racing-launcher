using System.Diagnostics;
using System.Text.RegularExpressions;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Sega Model 3 games (Daytona USA 2) in Supermodel.
/// Solo runs from Supermodel's own folder. Linked runs one copy per seat, each from its own folder under
/// runtime/link/supermodel/seatN (its own Config/, NVRAM/ and log), joined in a two-cabinet ring over localhost.
/// The game's own LINK ID (MASTER on seat 1, SLAVE on seat 2) lives in each seat's NVRAM.
/// </summary>
public sealed class SupermodelRunner : IRunner
{
    public string Id => "supermodel";

    const int BasePort = 1970;

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(EmulatorId(game));
        if (exe == null) problems.Add($"Supermodel (\"{EmulatorId(game)}\") isn't set up in config/emulators.json.");
        else if (!File.Exists(exe)) problems.Add($"Supermodel not found at {exe}");

        if (string.IsNullOrWhiteSpace(game.Rom)) problems.Add("No ROM set in config/games.json.");
        else if (!File.Exists(config.Resolve(game.Rom))) problems.Add($"ROM not found at {config.Resolve(game.Rom)}");
        return problems;
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Split)
            throw new NotSupportedException("Supermodel has no split screen; use Linked.");

        var exe = config.EmulatorPath(EmulatorId(game))!;
        var home = Path.GetDirectoryName(exe)!;

        if (mode == PlayMode.Solo)
        {
            // Solo reads Config/Supermodel.ini as it is (JOY1 = the first DirectInput controller).
            if (game.UsesTwinStick)
            {
                // The twin-stick lines are the only ones that name the stick, so they can be rewritten in place.
                var main = Path.Combine(home, "Config", "Supermodel.ini");
                File.WriteAllText(main, TwinStickIni(File.ReadAllText(main), JoystickNumber(seats[0]), config.Seats.TwinStickLayout, keepJoy: 1));
            }
            else
            {
                // The driving controls name the seat's wheel by its JOYn and its own layout (seat 1's G923 has its
                // pedals and buttons elsewhere than the vJoy wheel), so they are rewritten for this seat.
                var main = Path.Combine(home, "Config", "Supermodel.ini");
                File.WriteAllText(main, WheelIni(File.ReadAllText(main), JoystickNumber(seats[0]), seats[0].Wheel));
            }
            var psi = Start(exe, home, game, config, seats[0]);
            return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seats[0].SeatId}", psi, seats[0].Area, PlaceWindow: true, seats[0].Audio)]);
        }

        var instances = new List<InstancePlan>();
        for (int i = 0; i < seats.Count; i++)
        {
            var seat = seats[i];
            var dir = PrepareSeatFolder(config, home, EmulatorId(game), seat, game);
            // Its own copy of the exe: Windows keeps the per-app audio output per exe path.
            var seatExe = CopyIfNewer(exe, Path.Combine(dir, Path.GetFileName(exe)));
            foreach (var dll in Directory.GetFiles(home, "*.dll")) CopyIfNewer(dll, Path.Combine(dir, Path.GetFileName(dll)));
            // Supermodel reads DirectInput devices only while its window is in front, so of two seats only one
            // could play (Virtual-On OT, 2026-11-13/14). tools/dinput-bg makes it read them in the background:
            // every device shared, except the seat's own wheel (DINPUT_BG_EXCLUSIVE), which stays exclusive for
            // force feedback (exclusive + background is allowed for joysticks; 2026-11-17, Sega Rally).
            // (It is a version.dll: Supermodel gets DirectInput through COM, so a dinput8.dll beside it isn't loaded.)
            File.Delete(Path.Combine(dir, "dinput8.dll")); // first try, 2026-11-14
            foreach (var name in new[] { "version.dll", "version_orig.dll" })
                CopyIfNewer(config.Resolve($@"tools\dinput-bg\x64\{name}"), Path.Combine(dir, name));
            var psi = Start(seatExe, dir, game, config, seat);
            if (!game.UsesTwinStick && !string.IsNullOrEmpty(seat.JoystickMatch))
                psi.Environment["DINPUT_BG_EXCLUSIVE"] = seat.JoystickMatch;
            // A ring of two: each listens on its own port and connects to the other's. The Model 2 build connects
            // through SupermodelLinkRelay instead, which opens only once the copy behind it can take the link.
            bool relay = EmulatorId(game) == "supermodel-m2";
            int next = (i + 1) % seats.Count;
            psi.ArgumentList.Add("-net");
            psi.ArgumentList.Add($"-port-in={BasePort + i}");
            psi.ArgumentList.Add($"-port-out={(relay ? SupermodelLinkRelay.RelayBasePort : BasePort) + next}");
            psi.ArgumentList.Add("-address-out=127.0.0.1");
            var label = $"Seat {seat.SeatId}";
            instances.Add(new InstancePlan(label, psi, seat.Area, PlaceWindow: true, seat.Audio,
                Companion: relay ? SupermodelLinkRelay.For(label, dir, SupermodelLinkRelay.RelayBasePort + i, BasePort + i) : null));
        }
        return new LaunchPlan(game, mode, instances);
    }

    /// <summary>
    /// The emulators.json entry: games.json "emulator" if set. Virtual-On (Model 2) uses "supermodel-m2", a newer
    /// Supermodel with Model 2 support and the SHARC fix for its shadows, so the Model 3 games keep their build.
    /// </summary>
    string EmulatorId(GameProfile game) => string.IsNullOrWhiteSpace(game.Emulator) ? Id : game.Emulator;

    internal static string CopyIfNewer(string src, string dst)
    {
        if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) != File.GetLastWriteTimeUtc(dst)) File.Copy(src, dst, true);
        return dst;
    }

    static ProcessStartInfo Start(string exe, string workingDir, GameProfile game, LauncherConfig config, SeatAssignment seat)
    {
        var a = seat.Area;
        var psi = new ProcessStartInfo(exe)
        {
            // Supermodel looks for Config/ and NVRAM/ in its working directory.
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true, // hides its console window
        };
        psi.ArgumentList.Add(config.Resolve(game.Rom!));
        psi.ArgumentList.Add("-input-system=dinput"); // the wheels are DirectInput FFB wheels (vJoy / a native wheel)
        psi.ArgumentList.Add("-borderless");
        psi.ArgumentList.Add($"-res={a.Width},{a.Height}");
        psi.ArgumentList.Add($"-window-pos={a.X},{a.Y}");
        // With force feedback on, Supermodel takes its joysticks in exclusive mode, which DirectInput only gives the
        // window in front: in Linked the unfocused seat then reads nothing (Virtual-On OT seat 1 couldn't press
        // Start, 2026-11-13). The twin sticks have no force feedback anyway.
        if (game.UsesTwinStick) psi.ArgumentList.Add("-no-force-feedback");
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Makes runtime/link/&lt;emulator id&gt;/seatN (supermodel, or supermodel-m2 for Virtual-On). Config is rewritten
    /// from Supermodel's own every launch, with every JOY1_ binding moved to this seat's controller; NVRAM (the
    /// game's settings) is copied only once.
    /// </summary>
    static string PrepareSeatFolder(LauncherConfig config, string home, string emulatorId, SeatAssignment seat, GameProfile game)
    {
        var dir = Path.Combine(config.Root, "runtime", "link", emulatorId, $"seat{seat.SeatId}");
        foreach (var sub in new[] { "Config", "NVRAM", "Saves" }) Directory.CreateDirectory(Path.Combine(dir, sub));

        foreach (var file in Directory.GetFiles(Path.Combine(home, "Config")))
        {
            var dest = Path.Combine(dir, "Config", Path.GetFileName(file));
            if (Path.GetFileName(file).Equals("Supermodel.ini", StringComparison.OrdinalIgnoreCase))
            {
                // Supermodel numbers DirectInput controllers from 1 in DirectInput's own order: find where this
                // seat's wheel is in that order (seats.json "joystick"); else seats.json's slot (counting from 0).
                var ini = File.ReadAllText(file);
                int joy = JoystickNumber(seat);
                ini = Regex.Replace(ini, @"\bJOY1_", $"JOY{joy}_");
                // Twin-stick games: joy is the seat's twin stick; Start comes from it alone (the wheel's Start
                // button number is a lever button on the stick).
                if (game.UsesTwinStick) ini = TwinStickIni(ini, joy, config.Seats.TwinStickLayout, keepJoy: null);
                else ini = WheelIni(ini, joy, seat.Wheel);
                File.WriteAllText(dest, ini);
            }
            else File.Copy(file, dest, overwrite: true);
        }

        foreach (var nv in Directory.GetFiles(Path.Combine(home, "NVRAM"), "*.nv"))
        {
            var dest = Path.Combine(dir, "NVRAM", Path.GetFileName(nv));
            if (!File.Exists(dest)) File.Copy(nv, dest);
        }

        var assets = Path.Combine(home, "Assets");
        if (Directory.Exists(assets) && !Directory.Exists(Path.Combine(dir, "Assets")))
            CopyDir(assets, Path.Combine(dir, "Assets"));
        return dir;
    }

    /// <summary>
    /// Points Supermodel's twin-stick inputs (Virtual-On; only twin-stick games read them) at joystick
    /// <paramref name="stick"/>: left lever = its hat, right lever, triggers and turbos = its buttons, and adds its
    /// Start button to InputStart1. Keyboard keys on those lines stay. Input mappings are only read from [ Global ],
    /// so this can't be a per-game section. InputStart1 also keeps the JOY<paramref name="keepJoy"/> buttons (the
    /// wheel's Start for the driving games); every other joystick's Start is dropped.
    /// </summary>
    internal static string TwinStickIni(string ini, int stick, TwinStickLayout l, int? keepJoy)
    {
        string J(string what) => $"JOY{stick}_{what}";
        string B(int n) => J($"BUTTON{n}");
        var binds = new Dictionary<string, string?>
        {
            ["InputTwinJoyUp1"] = J("POV1_UP"), ["InputTwinJoyDown1"] = J("POV1_DOWN"),
            ["InputTwinJoyLeft1"] = J("POV1_LEFT"), ["InputTwinJoyRight1"] = J("POV1_RIGHT"),
            ["InputTwinJoyUp2"] = B(l.RightUp), ["InputTwinJoyDown2"] = B(l.RightDown),
            ["InputTwinJoyLeft2"] = B(l.RightLeft), ["InputTwinJoyRight2"] = B(l.RightRight),
            ["InputTwinJoyShot1"] = B(l.LeftTrigger), ["InputTwinJoyTurbo1"] = B(l.LeftTurbo),
            ["InputTwinJoyShot2"] = B(l.RightTrigger), ["InputTwinJoyTurbo2"] = B(l.RightTurbo),
            ["InputStart1"] = B(l.Start),
            ["InputCoin1"] = null, // the wheel's Back (7) is a right-lever button on the stick: keys only
        };
        foreach (var (key, joyBind) in binds)
        {
            var line = new Regex($@"^(?<head>\s*{key}\s*=\s*)""(?<value>[^""]*)""", RegexOptions.Multiline);
            var m = line.Match(ini);
            if (!m.Success) { Log.Warn($"  Supermodel.ini has no {key} line; twin stick input left out"); continue; }
            var keep = m.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.StartsWith("JOY", StringComparison.OrdinalIgnoreCase) ||
                            (key is "InputStart1" or "InputCoin1" && keepJoy is int k && k != stick && t.StartsWith($"JOY{k}_", StringComparison.OrdinalIgnoreCase)));
            var value = string.Join(",", joyBind == null ? keep : keep.Append(joyBind));
            ini = ini[..m.Index] + m.Groups["head"].Value + $"\"{value}\"" + ini[(m.Index + m.Length)..];
        }
        Log.Info($"  Supermodel twin stick = JOY{stick}");
        return ini;
    }

    /// <summary>
    /// Points Supermodel's driving controls at joystick <paramref name="joy"/> with layout <paramref name="l"/>:
    /// steering, pedals (with the axis rest values: Supermodel assumes an axis rests in the middle), Start (every
    /// Start button: the G923's Options and R2), coin, paddle shifts, the four view buttons and the extra buttons
    /// (handbrake etc.). Keyboard keys on those lines stay; every joystick binding on them is replaced.
    /// Supermodel names: axes XAXIS YAXIS ZAXIS RXAXIS RYAXIS RZAXIS SLIDER1 SLIDER2, rest-value keys
    /// InputJoy{n}{X Y Z RX RY RZ S1 S2}{Min,Off,Max}Val. A reversed pedal (rest at +32767) is bound _NEG with
    /// OffVal 32767, so the whole travel counts.
    /// </summary>
    internal static string WheelIni(string ini, int joy, JoystickLayout l)
    {
        static (string Bind, string Id) Names(string a) => JoystickLayout.NormAxis(a) switch
        {
            "X" => ("XAXIS", "X"), "Y" => ("YAXIS", "Y"), "Z" => ("ZAXIS", "Z"),
            "Rx" => ("RXAXIS", "RX"), "Ry" => ("RYAXIS", "RY"), "Rz" => ("RZAXIS", "RZ"),
            "Slider0" => ("SLIDER1", "S1"), _ => ("SLIDER2", "S2"),
        };
        string J(string what) => $"JOY{joy}_{what}";
        string B(int n) => J($"BUTTON{n}");
        string Pedal(string a) => J(Names(a).Bind + (l.PedalsReversed ? "_NEG" : "_POS"));
        var binds = new Dictionary<string, string[]>
        {
            ["InputSteering"] = [J("XAXIS")],
            ["InputAccelerator"] = [Pedal(l.GasAxis)],
            ["InputBrake"] = [Pedal(l.BrakeAxis)],
            ["InputStart1"] = l.StartButtons.Select(B).ToArray(),
            ["InputCoin1"] = [B(l.Back)],
            ["InputGearShiftUp"] = [B(l.RightShoulder)],
            ["InputGearShiftDown"] = [B(l.LeftShoulder)],
            ["InputVR1"] = [B(l.A)], ["InputVR2"] = [B(l.B)], ["InputVR3"] = [B(l.X)], ["InputVR4"] = [B(l.Y)],
            ["InputViewChange"] = [B(l.A)], ["InputHandBrake"] = [B(l.B)], ["InputRearBrake"] = [B(l.B)],
            ["InputMusicSelect"] = [B(l.X)],
        };
        foreach (var (key, joyBinds) in binds)
        {
            var line = new Regex($@"^(?<head>\s*{key}\s*=\s*)""(?<value>[^""]*)""", RegexOptions.Multiline);
            var m = line.Match(ini);
            if (!m.Success) { Log.Warn($"  Supermodel.ini has no {key} line; wheel input left out"); continue; }
            var keep = m.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.StartsWith("JOY", StringComparison.OrdinalIgnoreCase));
            ini = ini[..m.Index] + m.Groups["head"].Value + $"\"{string.Join(",", keep.Concat(joyBinds))}\"" + ini[(m.Index + m.Length)..];
        }
        foreach (var a in new[] { l.GasAxis, l.BrakeAxis })
        {
            var id = Names(a).Id;
            int rest = l.PedalsReversed ? 32767 : -32768;
            ini = SetValue(ini, $"InputJoy{joy}{id}MinVal", "-32768");
            ini = SetValue(ini, $"InputJoy{joy}{id}OffVal", rest.ToString());
            ini = SetValue(ini, $"InputJoy{joy}{id}MaxVal", "32767");
        }
        Log.Info($"  Supermodel wheel = JOY{joy} (gas {Pedal(l.GasAxis)}, brake {Pedal(l.BrakeAxis)}, start {string.Join("/", binds["InputStart1"])})");
        return ini;
    }

    /// <summary>Sets "key = value" in the ini (replacing the line, or adding it after the last InputJoy line).</summary>
    static string SetValue(string ini, string key, string value)
    {
        var line = new Regex($@"^\s*{key}\s*=.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var m = line.Match(ini);
        if (m.Success) return ini[..m.Index] + $"{key} = {value}" + ini[(m.Index + m.Length)..].TrimStart(' ');
        var last = new Regex(@"^\s*InputJoy\d+\w+Val\s*=.*$", RegexOptions.Multiline).Matches(ini).LastOrDefault();
        int at = last != null ? last.Index + last.Length : ini.Length;
        var nl = ini.Contains("\r\n") ? "\r\n" : "\n";
        return ini[..at] + nl + $"{key} = {value}" + ini[at..];
    }

    /// <summary>The seat's wheel's JOYn number under -input-system=dinput (its twin stick's, for twin-stick games).</summary>
    internal static int JoystickNumber(SeatAssignment seat, string who = "Supermodel")
    {
        try
        {
            if (Input.DirectInputDevices.UsableMatch(seat.JoystickMatch, $"Seat {seat.SeatId}") is { } m)
            {
                var all = Input.DirectInputDevices.List();
                int i = all.FindIndex(d => d.Path.Contains(m, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    Log.Info($"  Seat {seat.SeatId}: {who} JOY{i + 1} = {all[i].InstanceName} ({all[i].Path})");
                    return i + 1;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"  Seat {seat.SeatId}: {ex.Message}"); }
        return seat.XInputSlot + 1;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
