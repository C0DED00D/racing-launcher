using System.Xml.Linq;
using Launcher.Core.Config;
using Launcher.Core.Input;

namespace Launcher.Core.Runners;

/// <summary>
/// Points a TeknoParrot user profile at the seat's DirectInput wheel: "Input API" = DirectInput, and every bound
/// control gets a DirectInputButton on that wheel (by its DirectInput instance GUID, which is unique per device even
/// for identical wheels). The controls come from the profile's own XInput bindings (as scripts/tp-*-profile.ps1
/// wrote them), translated through seats.json "joystick", so any TeknoParrot game converts the same way.
/// DirectInput offsets as TeknoParrot stores them: axes X 0, Y 4, Z 8, RX 12, RY 16, RZ 20; hat 32 (+ PovDirection
/// in hundredths of a degree); button N (1-based) = 47 + N.
/// </summary>
public static class TpWheelProfile
{
    public static void Apply(string profilePath, SeatAssignment seat, JoystickLayout j)
    {
        var dev = DirectInputDevices.Find(seat.JoystickMatch);
        if (dev == null)
        {
            Log.Warn($"  TeknoParrot: no DirectInput wheel matching \"{seat.JoystickMatch}\"; {Path.GetFileName(profilePath)} left as it was");
            return;
        }
        var doc = XDocument.Load(profilePath);
        var g = doc.Root!;
        foreach (var f in g.Descendants("FieldInformation").Where(f => (string?)f.Element("FieldName") == "Input API"))
            f.SetElementValue("FieldValue", "DirectInput");

        int converted = 0;
        foreach (var jb in g.Descendants("JoystickButtons"))
        {
            var xi = jb.Element("XInputButton");
            if (xi == null) continue;
            var di = Translate(xi, j);
            if (di == null) continue;
            jb.Element("DirectInputButton")?.Remove();
            var e = new XElement("DirectInputButton",
                new XElement("Button", di.Value.Button),
                new XElement("IsAxis", di.Value.IsAxis ? "true" : "false"),
                new XElement("IsAxisMinus", di.Value.Minus ? "true" : "false"),
                new XElement("IsFullAxis", di.Value.Full ? "true" : "false"),
                new XElement("PovDirection", di.Value.Pov),
                new XElement("IsReverseAxis", di.Value.Reverse ? "true" : "false"),
                new XElement("JoystickGuid", dev.Instance.ToString()));
            // Keep TeknoParrot's element order: DirectInputButton comes before XInputButton.
            xi.AddBeforeSelf(e);
            jb.SetElementValue("BindNameDi", $"{dev.InstanceName} {di.Value.Label}");
            converted++;
        }
        doc.Save(profilePath);
        Log.Info($"  TeknoParrot: {Path.GetFileName(profilePath)}: {converted} controls on \"{dev.InstanceName}\" {dev.Instance} ({dev.Path})");
    }

    readonly record struct Di(int Button, bool IsAxis, bool Minus, bool Full, int Pov, bool Reverse, string Label);

    static Di? Translate(XElement xi, JoystickLayout j)
    {
        bool B(string n) => string.Equals((string?)xi.Element(n), "true", StringComparison.OrdinalIgnoreCase);
        static int AxisOffset(string a) => JoystickLayout.DiOffset(a);
        if (B("IsLeftThumbX")) return new Di(0, true, B("IsAxisMinus"), false, 0, false, "X axis (steering)");
        if (B("IsRightTrigger")) return new Di(AxisOffset(j.GasAxis), true, false, true, 0, j.PedalsReversed, $"{j.GasAxis} (gas)");
        if (B("IsLeftTrigger")) return new Di(AxisOffset(j.BrakeAxis), true, false, true, 0, j.PedalsReversed, $"{j.BrakeAxis} (brake)");
        if (!B("IsButton")) return null;
        int code = (ushort)short.Parse((string?)xi.Element("ButtonCode") ?? "0");
        int Btn(int n) => 47 + n;
        return code switch
        {
            0x0001 => new Di(32, false, false, false, 0, false, "hat up"),
            0x0008 => new Di(32, false, false, false, 9000, false, "hat right"),
            0x0002 => new Di(32, false, false, false, 18000, false, "hat down"),
            0x0004 => new Di(32, false, false, false, 27000, false, "hat left"),
            // One button per control: the G923's R2 (JoystickLayout.OneStart).
            0x0010 => new Di(Btn(j.OneStart), false, false, false, 0, false, $"button {j.OneStart}"),
            0x0020 => new Di(Btn(j.Back), false, false, false, 0, false, $"button {j.Back}"),
            0x0100 => new Di(Btn(j.LeftShoulder), false, false, false, 0, false, $"button {j.LeftShoulder}"),
            0x0200 => new Di(Btn(j.RightShoulder), false, false, false, 0, false, $"button {j.RightShoulder}"),
            0x1000 => new Di(Btn(j.A), false, false, false, 0, false, $"button {j.A}"),
            0x2000 => new Di(Btn(j.B), false, false, false, 0, false, $"button {j.B}"),
            0x4000 => new Di(Btn(j.X), false, false, false, 0, false, $"button {j.X}"),
            0x8000 => new Di(Btn(j.Y), false, false, false, 0, false, $"button {j.Y}"),
            _ => null,
        };
    }
}
