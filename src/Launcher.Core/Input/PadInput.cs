namespace Launcher.Core.Input;

/// <summary>
/// Controller inputs as one bit mask. The low 16 bits are XInput's own buttons;
/// the rest are analogue controls turned into on/off "virtual buttons" so a wheel can drive the menu.
/// </summary>
[Flags]
public enum PadInput : uint
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,

    // Virtual buttons from analogue controls. On a 360 wheel, the wheel is the left stick X axis,
    // the gas pedal is the right trigger and the brake is the left trigger.
    SteerLeft = 1u << 16,
    SteerRight = 1u << 17,
    StickUp = 1u << 18,
    StickDown = 1u << 19,
    Gas = 1u << 20,
    Brake = 1u << 21,

    /// <summary>Inputs that auto-repeat while held (menu navigation).</summary>
    Repeating = DPadUp | DPadDown | DPadLeft | DPadRight | SteerLeft | SteerRight | StickUp | StickDown,
}

public readonly record struct PadEvent(int Slot, PadInput Input, bool IsRepeat);

public readonly record struct PadInfo(int Slot, bool Connected, XInputSubType SubType, bool IsJoystick = false, string Name = "", bool IsTwinStick = false);
