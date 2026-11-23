using Launcher.Core.Native;

namespace Launcher.Core.Display;

/// <summary>A monitor's position on the desktop, in physical pixels.</summary>
public sealed record MonitorInfo(int Index, string DeviceName, bool IsPrimary, int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{DeviceName} {Width}x{Height} at {X},{Y}{(IsPrimary ? " (primary)" : "")}";
}

/// <summary>A rectangle on the desktop, in physical pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public static ScreenRect Of(MonitorInfo m) => new(m.X, m.Y, m.Width, m.Height);
}

public static class Monitors
{
    /// <summary>All monitors, primary first. Needs a per-monitor DPI aware process to report true pixels.</summary>
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var raw = new List<Win32.MONITORINFOEX>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(h, ref info)) raw.Add(info);
            return true;
        }, IntPtr.Zero);

        return raw
            .OrderByDescending(m => (m.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.rcMonitor.Left)
            .Select((m, i) => new MonitorInfo(i, m.szDevice, (m.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                m.rcMonitor.Left, m.rcMonitor.Top, m.rcMonitor.Width, m.rcMonitor.Height))
            .ToList();
    }

    public static MonitorInfo Primary() => GetAll()[0];

    /// <summary>Resolves a seats.json monitor name: "primary", "secondary", or an index. Null if it doesn't exist.</summary>
    public static MonitorInfo? Find(string name)
    {
        var all = GetAll();
        return name.Trim().ToLowerInvariant() switch
        {
            "primary" => all[0],
            "secondary" => all.Count > 1 ? all[1] : null,
            var s when int.TryParse(s, out int i) => i >= 0 && i < all.Count ? all[i] : null,
            _ => null,
        };
    }
}
