using System.Diagnostics;
using Launcher.Core.Config;

namespace Launcher.Core.Wheels;

/// <summary>
/// Makes the DirectInput wheel a "game controller" for SDL programs that only take game controllers (xemu, Xenia,
/// Project R): an SDL_GAMECONTROLLERCONFIG mapping in the Xbox layout games expect (A B X Y, shoulders = paddles,
/// left stick X = steering, right trigger = gas, left trigger = brake, hat = d-pad).
/// SDL picks the mapping by the device's GUID, which is bus + CRC-16 of its name + vendor + product + version. SDL2
/// names vJoy "vJoy Device" (0300f800...), SDL3 "Shaul Eizikovich vJoy - Virtual Joystick" (030088bd..., both
/// checked 2026-11-07); a GUID with CRC 0 matches any name of that vendor/product, which covers a new wheel's name
/// once seats.json "joystick" sdlGuid is set to it.
/// </summary>
public static class WheelSdl
{
    public static string Mapping(JoystickLayout j)
    {
        string B(int n) => n >= 1 ? $"b{n - 1}" : "";
        string inv = j.PedalsReversed ? "~" : "";
        // One button per pad control: Start = OneStart (the G923's R2).
        var parts = new List<string>
        {
            $"a:{B(j.A)}", $"b:{B(j.B)}", $"x:{B(j.X)}", $"y:{B(j.Y)}",
            $"leftshoulder:{B(j.LeftShoulder)}", $"rightshoulder:{B(j.RightShoulder)}",
            $"back:{B(j.Back)}", $"start:{B(j.OneStart)}", $"guide:{B(j.Guide)}",
            "dpup:h0.1", "dpright:h0.2", "dpdown:h0.4", "dpleft:h0.8",
            $"leftx:a{j.SdlAxis("X")}", $"righttrigger:a{j.SdlAxis(j.GasAxis)}{inv}", $"lefttrigger:a{j.SdlAxis(j.BrakeAxis)}{inv}",
            "platform:Windows",
        };
        var body = string.Join(",", parts.Where(p => !p.EndsWith(':'))) + ",";
        var guids = new[] { j.SdlGuid, j.Sdl2Guid, ZeroCrc(j.SdlGuid) }.Distinct();
        return string.Join("\n", guids.Select(g => $"{g},Racing wheel,{body}"));
    }

    /// <summary>The same GUID with the name CRC (bytes 2-3) zeroed.</summary>
    static string ZeroCrc(string guid) => guid.Length == 32 ? guid[..4] + "0000" + guid[8..] : guid;

    /// <summary>
    /// The twin stick as an Xbox pad (Virtual-On Force on Xenia), in the Hori Twin Stick EX layout: left lever = left
    /// stick (from the hat), right lever = right stick, triggers = LT/RT, turbos = LB/RB, Start. The stick has no face
    /// buttons, so the triggers also press B (left) and A (right) for the menus.
    /// </summary>
    public static string TwinStickMapping(TwinStickLayout l)
    {
        string B(int n) => $"b{n - 1}";
        var parts = new[]
        {
            "-leftx:h0.8", "+leftx:h0.2", "-lefty:h0.1", "+lefty:h0.4",
            $"-rightx:{B(l.RightLeft)}", $"+rightx:{B(l.RightRight)}", $"-righty:{B(l.RightUp)}", $"+righty:{B(l.RightDown)}",
            $"lefttrigger:{B(l.LeftTrigger)}", $"righttrigger:{B(l.RightTrigger)}",
            $"leftshoulder:{B(l.LeftTurbo)}", $"rightshoulder:{B(l.RightTurbo)}",
            $"a:{B(l.RightTrigger)}", $"b:{B(l.LeftTrigger)}", $"start:{B(l.Start)}",
            "platform:Windows",
        };
        var body = string.Join(",", parts) + ",";
        return string.Join("\n", new[] { l.Sdl2Guid, ZeroCrc(l.Sdl2Guid) }.Distinct().Select(g => $"{g},Twin stick,{body}"));
    }

    public static void ApplyTwinStick(ProcessStartInfo psi, TwinStickLayout l)
    {
        psi.Environment["SDL_GAMECONTROLLERCONFIG"] = TwinStickMapping(l);
        psi.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
    }

    /// <summary>Gives a program the mapping (and background input, which SDL drops for unfocused windows otherwise).</summary>
    public static void Apply(ProcessStartInfo psi, JoystickLayout j)
    {
        psi.Environment["SDL_GAMECONTROLLERCONFIG"] = Mapping(j);
        psi.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
    }
}
