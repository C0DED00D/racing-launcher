using System.Runtime.InteropServices;
using Launcher.Core.Config;
using Launcher.Core.Runners;
using Microsoft.Win32.SafeHandles;

namespace Launcher.Core.Wheels;

/// <summary>
/// Sets a Logitech wheel's rotation range for each game: games.json "wheelRange", else the seat wheel's "range"
/// (seats.json; the G923's is 270, the 360 wheel's travel the user was used to). The command is the classic Logitech
/// one (Linux lg4ff, G25 to G923): F8 81 &lt;lo&gt; &lt;hi&gt; 00 00 00, written to the wheel's joystick interface
/// (usage page 1, the 17-byte output report, report id 0). It rescales the steering axis (full lock at half the range
/// each way); it does not add a physical end stop. Checked 2026-11-20 on the G923 for PlayStation and PC (046D:C266)
/// under G HUB: with G HUB at 900, half a turn read full lock after the command. G HUB puts its own value back when its
/// window is in front, so the command is sent at launch and repeated a few times.
/// (HID++ on interface 1 and SET_REPORT on the control pipe do not work on this wheel; docs/g923.md.)
/// </summary>
public static class WheelRange
{
    const int LogitechVendor = 0x046D;
    static readonly TimeSpan[] Repeats = [TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>Sends each seat's range for this game in the background (and again a few times).</summary>
    public static void ApplyInBackground(IReadOnlyList<SeatAssignment> seats, GameProfile game)
    {
        if (game.UsesTwinStick) return;
        var jobs = seats.Select(s => (Seat: s.SeatId, Wheel: s.Wheel, Degrees: game.WheelRange ?? s.Wheel.Range))
            .Where(j => j.Degrees > 0 && j.Wheel.VendorId == LogitechVendor && j.Wheel.ProductId != 0).ToList();
        if (jobs.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var wait in Repeats)
            {
                await Task.Delay(wait);
                foreach (var j in jobs)
                {
                    var result = Set(j.Wheel.VendorId, j.Wheel.ProductId, j.Degrees);
                    if (wait == TimeSpan.Zero || !result.StartsWith("range")) Log.Info($"  Seat {j.Seat}: wheel {result}");
                }
            }
        });
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] b, int n, out int written, IntPtr o);

    [StructLayout(LayoutKind.Sequential)]
    struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort A, B, C, D, E, F, G, H, I, J;
    }

    /// <summary>Sends the range command to every attached wheel with this USB id; a line for the log.</summary>
    public static string Set(int vendorId, int productId, int degrees)
    {
        degrees = Math.Clamp(degrees, 40, 900);
        int sent = 0;
        foreach (var path in HidPaths(vendorId, productId))
        {
            using var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!HidD_GetPreparsedData(h, out var pp)) continue;
            HidP_GetCaps(pp, out var caps);
            HidD_FreePreparsedData(pp);
            if (caps.UsagePage != 1 || caps.OutputLength < 8) continue; // the joystick interface only
            var report = new byte[caps.OutputLength]; // [0] = report id 0
            report[1] = 0xF8; report[2] = 0x81; report[3] = (byte)(degrees & 0xFF); report[4] = (byte)(degrees >> 8);
            if (WriteFile(h, report, report.Length, out _, IntPtr.Zero)) sent++;
        }
        return sent > 0 ? $"range {degrees} degrees" : $"range not set: no writable Logitech wheel {vendorId:X4}:{productId:X4}";
    }

    static IEnumerable<string> HidPaths(int vendorId, int productId)
    {
        var hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        var vidpid = $"vid_{vendorId:x4}&pid_{productId:x4}";
        var set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var data = new Iface { Size = (uint)Marshal.SizeOf<Iface>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal((int)need);
                string? path = null;
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, need, out _, IntPtr.Zero)) path = Marshal.PtrToStringUni(buf + 4);
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (path != null && path.Contains(vidpid, StringComparison.OrdinalIgnoreCase)) yield return path;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint i, ref Iface data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref Iface data, IntPtr detail, uint size, out uint need, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [StructLayout(LayoutKind.Sequential)] struct Iface { public uint Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
}
