using System.Runtime.InteropServices;

namespace AtGamesMouse;

/// <summary>P/Invoke for Raw Input (user32), HID parsing (hid.dll) and SendInput.</summary>
internal static class Native
{
    public const int WM_INPUT = 0x00FF;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const int GIDC_ARRIVAL = 1;
    public const int GIDC_REMOVAL = 2;

    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEHID = 2;

    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000b;

    public const ushort UsagePageGeneric = 0x01, UsageJoystick = 0x04, UsageGamepad = 0x05;
    public const ushort UsagePageButton = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr Device;
        public uint Type;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[]? data, ref uint size);

    /// <summary>RAWINPUTHEADER: dwType, dwSize, hDevice, wParam.</summary>
    public static int RawInputHeaderSize => 8 + 2 * IntPtr.Size;

    // ---- hid.dll ----
    public const int HidP_Input = 0;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, byte[] preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, byte[] preparsedData);

    // ---- SendInput ----
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>INPUT with the mouse member of the union (the largest one, so the size matches).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
