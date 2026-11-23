namespace Launcher.Core.Config;

public enum PlayMode
{
    /// <summary>One instance, fullscreen on the primary monitor.</summary>
    Solo,
    /// <summary>Two networked instances, one per seat.</summary>
    Linked,
    /// <summary>One instance, split screen with two controllers.</summary>
    Split,
}

public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string Runner { get; set; } = "";

    /// <summary>Game files (ROM zip, CHD, ISO or folder), relative to the repo root.</summary>
    public string? Rom { get; set; }

    /// <summary>For the "exe" runner: the program to start, relative to the repo root or absolute.</summary>
    public string? Exe { get; set; }

    /// <summary>For the "steam" runner: the Steam app id (steamapps/appmanifest_&lt;id&gt;.acf).</summary>
    public int? SteamAppId { get; set; }

    /// <summary>
    /// Optional: which config/emulators.json entry the runner uses instead of its own (e.g. "supermodel-m2", a newer
    /// Supermodel for Model 2 games, beside the Model 3 one).
    /// </summary>
    public string? Emulator { get; set; }

    public List<PlayMode> Modes { get; set; } = [PlayMode.Solo];
    public string? Color { get; set; }

    /// <summary>Extra command-line arguments appended by the runner.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Extra environment variables for the game's process (no runner reads it at the moment).</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Xenia: profile XUIDs to sign in at boot, one per player slot (Player 1 first).</summary>
    public List<string> Profiles { get; set; } = [];

    /// <summary>Xenia Linked: Live-enabled profile XUIDs, one per seat (seat 1 first).</summary>
    public List<string> LinkProfiles { get; set; } = [];

    /// <summary>A line shown under the mode chips when that mode is picked, keyed by mode name ("Linked").</summary>
    public Dictionary<string, string> ModeHints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? HintFor(PlayMode mode) =>
        ModeHints.TryGetValue(mode.ToString(), out var hint) && !string.IsNullOrWhiteSpace(hint) ? hint : null;

    /// <summary>TeknoParrot: the profile name (UserProfiles/&lt;name&gt;.xml), as in TeknoParrot's GameProfiles folder.</summary>
    public string? TpProfile { get; set; }

    /// <summary>TeknoParrot Linked: the game's own process name (e.g. "Rally"), whose window is placed per seat.</summary>
    public string? WindowProcess { get; set; }

    /// <summary>
    /// Flycast: a folder (repo-relative) of controller mapping files for this game. The game then runs from its own
    /// Flycast folder (runtime/flycast/&lt;id&gt; Solo, runtime/link/flycast/seatN Linked) with these files in
    /// mappings\, so they don't replace the wheel mappings the other Flycast games use.
    /// </summary>
    public string? FlycastMappings { get; set; }

    /// <summary>Xenia Linked: "live" (default; local Xenia Web Services, as Hurricane) or "systemlink".</summary>
    public string? XeniaLinkMode { get; set; }

    /// <summary>
    /// Wheel rotation range in degrees for this game (Logitech wheels; Wheels.WheelRange). Null = the seat wheel's own
    /// "range" (seats.json; 270 for the G923). The Steam sims use 900.
    /// </summary>
    public int? WheelRange { get; set; }

    /// <summary>Force feedback for this game (the wheel hub); null = defaults.</summary>
    public GameFfbSettings? Ffb { get; set; }

    /// <summary>
    /// The controller this game is played with: null = the seat's wheel; "twinStick" = the seat's Virtual-On twin
    /// stick (seats.json seat "twinStick", buttons in seats.json "twinStickLayout").
    /// </summary>
    public string? Controller { get; set; }

    public bool UsesTwinStick => string.Equals(Controller, "twinStick", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// games.json "ffb". rumble: what the game's XInput rumble means: "motors" (plain rumble, the default), "steer"
/// (a steering push carried on the two motors, as Supermodel sends it) or "off". strength multiplies the seat's
/// strength; invert turns the forces round for a game that pushes the wrong way.
/// </summary>
public sealed class GameFfbSettings
{
    public string? Rumble { get; set; }
    public double Strength { get; set; } = 1;
    public bool Invert { get; set; }
}

public sealed class GamesFile
{
    public List<GameProfile> Games { get; set; } = [];
}

public sealed class EmulatorEntry
{
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public string? Url { get; set; }
}

public sealed class EmulatorsFile
{
    public Dictionary<string, EmulatorEntry> Emulators { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SeatConfig
{
    public int Id { get; set; }

    /// <summary>
    /// This seat's controller number, 0-based: its place among the DirectInput wheels (JOYn = this + 1 in Supermodel)
    /// and, for anything still on XInput, its XInput slot.
    /// </summary>
    public int XInputSlot { get; set; }

    /// <summary>
    /// Part of this seat's wheel's device path or name, for programs that can pick a device by it (vJoy device N's
    /// path has "&amp;Col0N"). For a new wheel: part of its name, or of its USB path if both seats have the same model.
    /// </summary>
    public string? Joystick { get; set; }

    /// <summary>
    /// Part of this seat's twin stick's device path or name (games with "controller": "twinStick"). The Saturn
    /// adapter's two ports are one USB device with two interfaces: "pid_187a&amp;mi_00" = port 1, "&amp;mi_01" = port 2.
    /// </summary>
    public string? TwinStick { get; set; }

    /// <summary>"primary", "secondary", or a zero-based monitor index.</summary>
    public string Monitor { get; set; } = "primary";

    /// <summary>Part of the name of this seat's audio output (as in Windows Sound settings), e.g. "Speakers" or the TV's brand. Null: system default.</summary>
    public string? Audio { get; set; }

    /// <summary>
    /// This seat's wheel's layout (buttons, pedal axes, SDL names), when it isn't the wheel hub's vJoy wheel (the
    /// top-level "joystick" block). Seat 1's Logitech G923 since 2026-11-20.
    /// </summary>
    public JoystickLayout? Wheel { get; set; }
}

public sealed class SeatsFile
{
    public bool DevMode { get; set; } = true;
    public List<SeatConfig> Seats { get; set; } = [];

    /// <summary>How the menu reads DirectInput wheels (button numbers etc.). Defaults = the wheel hub's vJoy wheel.</summary>
    public JoystickLayout Joystick { get; set; } = new();

    /// <summary>The twin sticks' DirectInput buttons. Defaults = Sega Saturn Twin Stick on the Saturn USB adapter.</summary>
    public TwinStickLayout TwinStickLayout { get; set; } = new();

    /// <summary>The layout of seat <paramref name="seatId"/>'s wheel: its own "wheel" block, else the shared one.</summary>
    public JoystickLayout LayoutFor(int seatId) => Seats.FirstOrDefault(s => s.Id == seatId)?.Wheel ?? Joystick;

    /// <summary>Every wheel layout in use (the seats' own first), for the menu, which reads every wheel attached.</summary>
    public IReadOnlyList<JoystickLayout> AllLayouts() =>
        Seats.Select(s => s.Wheel).OfType<JoystickLayout>().Append(Joystick).Distinct().ToList();
}

/// <summary>
/// seats.json "twinStickLayout": DirectInput button numbers (1-based, as Windows' controller settings show them) of a
/// Virtual-On twin stick. Defaults measured 2026-11-12 on two Sega Saturn Twin Sticks through the "SS Controller
/// Adapter-Dinput" (0079:187A): the left lever is the hat (also buttons 13-16 = N E S W), the right lever is buttons,
/// each lever has a trigger and a turbo button.
/// </summary>
public sealed class TwinStickLayout
{
    public int RightUp { get; set; } = 5;
    public int RightRight { get; set; } = 7;
    public int RightDown { get; set; } = 2;
    public int RightLeft { get; set; } = 4;
    public int LeftTrigger { get; set; } = 6;
    public int LeftTurbo { get; set; } = 8;
    public int RightTrigger { get; set; } = 1;
    public int RightTurbo { get; set; } = 3;
    public int Start { get; set; } = 10;

    /// <summary>The adapter's USB vendor and product id: the menu reads joysticks with these as twin sticks.</summary>
    public int VendorId { get; set; } = 0x0079;
    public int ProductId { get; set; } = 0x187A;

    /// <summary>The adapter's SDL2 GUID (Xenia's SDL2 picks the game-controller mapping by it).</summary>
    public string Sdl2Guid { get; set; } = "03002b24790000007a18000000000000";

    /// <summary>The adapter's name as SDL reports it (Flycast's mapping file is named after it).</summary>
    public string SdlName { get; set; } = "SS Controller Adapter-Dinput";
}

/// <summary>
/// seats.json "joystick" (and a seat's own "wheel"): DirectInput button numbers (1-based, as Windows' controller
/// settings show them) and pedal axes, for the menu, Guide-twice exit and every game's mapping. Defaults are the wheel
/// hub's vJoy layout. Steering is the X axis. Axes are named as DirectInput does: "X" "Y" "Z" "Rx" "Ry" "Rz"
/// "Slider0" "Slider1" (older names: "R" = Rz, "U" = Rx, "V" = Ry). "axes" lists the axes the device has, in that
/// order: SDL numbers them the same way (a pedal on Rz of a wheel without Z is SDL axis 2).
/// </summary>
public sealed class JoystickLayout
{
    public int A { get; set; } = 1;
    public int B { get; set; } = 2;
    public int X { get; set; } = 3;
    public int Y { get; set; } = 4;
    public int LeftShoulder { get; set; } = 5;
    public int RightShoulder { get; set; } = 6;
    public int Back { get; set; } = 7;
    public int Start { get; set; } = 8;
    public int Guide { get; set; } = 11;

    /// <summary>
    /// More buttons that also mean Start (G923: R2, easier to reach). Games that take only one button per control get
    /// the first of these instead of <see cref="Start"/>.
    /// </summary>
    public List<int> StartAlso { get; set; } = [];

    public string GasAxis { get; set; } = "Y";
    public string BrakeAxis { get; set; } = "Z";
    public bool PedalsReversed { get; set; }
    public List<string> Axes { get; set; } = ["X", "Y", "Z", "Rx", "Ry", "Rz"];

    /// <summary>
    /// Rotation range in degrees the launcher sets on this wheel when a game starts (Logitech wheels only; games.json
    /// "wheelRange" overrides it per game). 0 = leave it as it is.
    /// </summary>
    public int Range { get; set; }

    /// <summary>The wheel's USB vendor/product id; the menu uses this layout for joysticks with these ids. 0 = any.</summary>
    public int VendorId { get; set; }
    public int ProductId { get; set; }

    /// <summary>The Start button for games that take one: the first "startAlso" button if any.</summary>
    public int OneStart => StartAlso.Count > 0 ? StartAlso[0] : Start;

    /// <summary>Every button that means Start.</summary>
    public IEnumerable<int> StartButtons => StartAlso.Prepend(Start).Where(b => b >= 1).Distinct();

    /// <summary>An axis name in DirectInput's spelling: X Y Z Rx Ry Rz Slider0 Slider1.</summary>
    public static string NormAxis(string a) => a.Trim().ToUpperInvariant() switch
    {
        "X" => "X", "Y" => "Y", "Z" => "Z",
        "RX" or "U" => "Rx", "RY" or "V" => "Ry", "RZ" or "R" => "Rz",
        "SLIDER0" or "SLIDER" or "SLIDER1" or "S1" => "Slider0", "SLIDER2" or "S2" => "Slider1",
        _ => throw new InvalidDataException($"seats.json: unknown axis \"{a}\" (use X Y Z Rx Ry Rz Slider0 Slider1)"),
    };

    /// <summary>The axis' number as SDL counts them (its place among the device's axes).</summary>
    public int SdlAxis(string a)
    {
        var n = NormAxis(a);
        int i = Axes.Select(NormAxis).ToList().IndexOf(n);
        return i >= 0 ? i : throw new InvalidDataException($"seats.json: axis {a} isn't in this wheel's \"axes\"");
    }

    /// <summary>The axis' offset in DirectInput's DIJOYSTATE (TeknoParrot stores bindings by it).</summary>
    public static int DiOffset(string a) => NormAxis(a) switch
    {
        "X" => 0, "Y" => 4, "Z" => 8, "Rx" => 12, "Ry" => 16, "Rz" => 20, "Slider0" => 24, _ => 28,
    };

    /// <summary>Only joysticks whose name contains this are used (empty = all).</summary>
    public string? NameContains { get; set; }

    /// <summary>The wheel's SDL3 GUID, for SDL3 games that bind by it (Daytona USA). vJoy's: the same for every device.</summary>
    public string SdlGuid { get; set; } = "030088bd34120000adbe000000000000";

    /// <summary>The same for SDL2 programs (xemu), which name the device differently ("vJoy Device").</summary>
    public string Sdl2Guid { get; set; } = "0300f80034120000adbe000000000000";

    /// <summary>The wheel's name as SDL reports it, for SDL games that map by name (Flycast).</summary>
    public string SdlName { get; set; } = "Shaul Eizikovich vJoy - Virtual Joystick";

    /// <summary>The SDL2 GUID without its name CRC: what old SDL2 (2.0.9, the FFB Arcade Plugin's) reports.</summary>
    public string Sdl2GuidNoCrc => Sdl2Guid.Length == 32 ? Sdl2Guid[..4] + "0000" + Sdl2Guid[8..] : Sdl2Guid;
}
