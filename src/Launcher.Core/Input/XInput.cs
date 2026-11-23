using System.Runtime.InteropServices;

namespace Launcher.Core.Input;

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
struct XInputCapabilities
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

public enum XInputSubType : byte
{
    Unknown = 0x00,
    Gamepad = 0x01,
    Wheel = 0x02,
    ArcadeStick = 0x03,
    FlightStick = 0x04,
    DancePad = 0x05,
    Guitar = 0x06,
    DrumKit = 0x08,
    ArcadePad = 0x13,
}

/// <summary>
/// XInput access. Uses the undocumented XInputGetStateEx (xinput1_4.dll, ordinal 100),
/// which is identical to XInputGetState except that it also reports the Guide button (0x0400).
/// Falls back to the public XInputGetState if the ordinal is missing.
/// </summary>
public static class XInput
{
    public const int MaxControllers = 4;
    const uint ErrorSuccess = 0;

    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    static extern uint XInputGetStateEx(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetStatePublic(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    static extern uint XInputGetCapabilitiesNative(uint userIndex, uint flags, out XInputCapabilities caps);

    static bool _useEx = true;

    /// <summary>True if the Guide button can be read on this system.</summary>
    public static bool GuideSupported => _useEx;

    public static bool TryGetState(int slot, out XInputState state)
    {
        if (_useEx)
        {
            try
            {
                return XInputGetStateEx((uint)slot, out state) == ErrorSuccess;
            }
            catch (EntryPointNotFoundException)
            {
                _useEx = false;
                Log.Warn("XInputGetStateEx not available; the Guide button can't be read.");
            }
        }
        return XInputGetStatePublic((uint)slot, out state) == ErrorSuccess;
    }

    public static XInputSubType GetSubType(int slot) =>
        XInputGetCapabilitiesNative((uint)slot, 0, out var caps) == ErrorSuccess
            ? (XInputSubType)caps.SubType
            : XInputSubType.Unknown;
}
